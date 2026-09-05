#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApiUrl,
    [Parameter(Mandatory)][string]$ReceiverUrl,
    [Parameter(Mandatory)][string]$DashboardUrl,
    [Parameter(Mandatory)][string[]]$ComposeArguments,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
function Compose([string[]]$Arguments) {
    & docker @ComposeArguments @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Recovery demo Compose failed ($LASTEXITCODE)." }
}
function Wait-For([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Receiver-Mode([string]$Mode) {
    $env:RECEIVER_MODE = $Mode
    Compose @('up', '-d', '--no-deps', 'receiver')
    Wait-For {
        try { (Invoke-WebRequest "$ReceiverUrl/health/ready" -TimeoutSec 2 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { $false }
    } 'Receiver did not restart.'
}
$priorMode = $env:RECEIVER_MODE
try {
    Receiver-Mode 'Reject'
    $key = 'recovery-' + [Guid]::NewGuid().ToString('N')
    $body = @{ destinationId = 'demo'; eventType = 'document.ready'; data = @{ documentId = $key } } | ConvertTo-Json -Compress
    $accepted = Invoke-WebRequest "$ApiUrl/events" -Method Post -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $key } -Body $body
    if ($accepted.StatusCode -ne 202) { throw 'Recovery input was not newly accepted.' }
    $id = ($accepted.Content | ConvertFrom-Json).deliveryId
    Wait-For { (Invoke-RestMethod "$ApiUrl/deliveries/$id").status -eq 'Failed' } 'Retry budget did not exhaust.'
    $failed = Invoke-RestMethod "$ApiUrl/deliveries/$id"
    if ($failed.attemptCount -ne 3 -or $failed.lastOutcome -ne 'RetryExhausted' -or @($failed.attempts | Where-Object httpStatus -ne 503).Count -ne 0) {
        throw 'Expected exactly three persisted 503 attempts and RetryExhausted.'
    }
    $failed | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $EvidenceDirectory 'exhausted.json')

    $search = [Uri]::EscapeDataString("@relaylab.delivery.id:$id")
    # Export is asynchronous and SIGKILL discards in-memory spans. Observe this generation before killing its exporter.
    Wait-For {
        $trace = Invoke-RestMethod "$DashboardUrl/api/telemetry/traces?search=$search"
        @($trace.data.resourceSpans.scopeSpans.spans | Where-Object name -eq 'transition').Count -eq 3
    } 'The dashboard did not ingest the exhausted generation before interruption.'

    Receiver-Mode 'CommitThenWaitOnce'
    $replayKey = 'replay-' + [Guid]::NewGuid().ToString('N')
    $replayed = Invoke-WebRequest "$ApiUrl/deliveries/$id/replay" -Method Post -Headers @{ 'Idempotency-Key' = $replayKey }
    $repeat = Invoke-WebRequest "$ApiUrl/deliveries/$id/replay" -Method Post -Headers @{ 'Idempotency-Key' = $replayKey }
    if ($replayed.StatusCode -ne 202 -or $repeat.StatusCode -ne 200 -or
        ($repeat.Content | ConvertFrom-Json).workId -ne ($replayed.Content | ConvertFrom-Json).workId) { throw 'Replay idempotency failed.' }
    Wait-For {
        $receipt = Invoke-WebRequest "$ReceiverUrl/receipts/$id" -SkipHttpErrorCheck
        $receipt.StatusCode -eq 200 -and ($receipt.Content | ConvertFrom-Json).effectCount -eq 1
    } 'Receiver did not durably commit the replay effect.'
    # SIGKILL is an actual process interruption. SQL already has Started and the receiver has committed its effect.
    Compose @('kill', '--signal', 'SIGKILL', 'worker')
    $interrupted = Invoke-RestMethod "$ApiUrl/deliveries/$id"
    if ($interrupted.status -ne 'Processing' -or $interrupted.attemptCount -ne 4 -or $interrupted.attempts[-1].outcome -ne 'Started') {
        throw 'The process kill missed the intended pre-completion boundary; do not count this run as crash evidence.'
    }
    $interrupted | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $EvidenceDirectory 'interrupted.json')
    Receiver-Mode 'Acknowledge' # new receiver process, retained SQL ledger
    Compose @('up', '-d', '--no-deps', 'worker')
    Wait-For { (Invoke-RestMethod "$ApiUrl/deliveries/$id").status -eq 'Delivered' } 'Expired claim did not recover.'
    $delivered = Invoke-RestMethod "$ApiUrl/deliveries/$id"
    $receipt = Invoke-RestMethod "$ReceiverUrl/receipts/$id"
    $unknown = @($delivered.attempts | Where-Object outcome -eq 'Interrupted')
    if ($delivered.attemptCount -ne 5 -or $delivered.generation -ne 1 -or $receipt.effectCount -ne 1 -or
        $unknown.Count -ne 1 -or $unknown[0].remoteOutcome -ne 'Unknown' -or $null -ne $unknown[0].completedUtc) {
        throw 'Unexpected recovered state, attempt history or receiver effects.'
    }
    $delivered | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $EvidenceDirectory 'recovered.json')

    Wait-For {
        $trace = Invoke-RestMethod "$DashboardUrl/api/telemetry/traces?search=$search"
        $spans = @($trace.data.resourceSpans.scopeSpans.spans)
        @($spans | Where-Object name -eq 'recover').Count -ge 1 -and @($spans | Where-Object name -eq 'transition').Count -ge 4
    } 'The Aspire dashboard did not ingest the recovery trace.'
    $trace = Invoke-RestMethod "$DashboardUrl/api/telemetry/traces?search=$search"
    $spans = @($trace.data.resourceSpans.scopeSpans.spans)
    foreach ($name in @('accept', 'publish', 'attempt', 'webhook', 'receive', 'transition', 'replay', 'recover')) {
        if ($name -notin $spans.name) { throw "Missing trace span: $name" }
    }
    $traceIds = @($spans.traceId | Select-Object -Unique)
    if ($traceIds.Count -ne 1) { throw 'Recovery spans do not share the persisted trace identity.' }
    $services = @($trace.data.resourceSpans.resource.attributes | Where-Object key -eq 'service.name' | ForEach-Object { $_.value.stringValue } | Select-Object -Unique)
    foreach ($service in @('relaylab-api', 'relaylab-worker', 'relaylab-receiver')) {
        if ($service -notin $services) { throw "Missing telemetry service: $service" }
    }
    $trace | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $EvidenceDirectory 'recovery-trace.json')
    $evidence = [ordered]@{ deliveryId = $id; status = $delivered.status; exhaustedAttempts = 3; totalAttempts = 5; replayGeneration = 1;
        replayFirstResponse = 202; replayRepeatResponse = 200; processInterruption = 'SIGKILL after receiver effect before sender completion';
        interruptedRemoteOutcome = 'Unknown'; receiverEffectCount = $receipt.effectCount; traceId = $traceIds[0]; traceSpanCount = $spans.Count; telemetryServices = $services }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDirectory 'recovery-result.json')
    $evidence | ConvertTo-Json -Depth 5
} finally {
    $env:RECEIVER_MODE = $priorMode
}
