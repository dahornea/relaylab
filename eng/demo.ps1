#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Run', 'Down', 'Reset', 'DeadLetters')][string]$Action = 'Run',
    [ValidatePattern('^[a-z][a-z0-9-]+$')][string]$ProjectName = 'relaylab',
    [int]$ApiPort = 5080,
    [int]$ReceiverPort = 5081,
    [int]$BrokerHealthPort = 5300
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $root '.env'
$compose = @('compose', '--project-name', $ProjectName, '--project-directory', $root, '--file', (Join-Path $root 'compose.yaml'), '--env-file', $envFile)

function Invoke-Compose([string[]]$Arguments) {
    & docker @compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE." }
}

function Wait-Http([string]$Url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -TimeoutSec 3 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Readiness timed out: $Url"
}

if (!(Test-Path -LiteralPath $envFile)) {
    if ($Action -ne 'Run') { throw 'No local .env exists. Run the demo first.' }
    $versions = Get-Content -Raw (Join-Path $root 'infra/versions.json') | ConvertFrom-Json
    $password = 'RL!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'a9'
    @(
        "SQL_PASSWORD=$password",
        "SQL_IMAGE=$($versions.sql)", "SERVICEBUS_IMAGE=$($versions.serviceBus)",
        "SDK_IMAGE=$($versions.sdk)", "RUNTIME_IMAGE=$($versions.aspnet)",
        "API_PORT=$ApiPort", "RECEIVER_PORT=$ReceiverPort", "BROKER_HEALTH_PORT=$BrokerHealthPort"
    ) | Set-Content -LiteralPath $envFile -Encoding utf8
    if (!$IsWindows) { & chmod 600 $envFile; if ($LASTEXITCODE -ne 0) { throw 'Could not restrict local secret file permissions.' } }
}
# Use saved ports when rerunning a retained demo. Never print the password.
$saved = @{}
foreach ($line in Get-Content -LiteralPath $envFile) {
    if ($line -match '^(API_PORT|RECEIVER_PORT|BROKER_HEALTH_PORT)=(\d+)$') { $saved[$Matches[1]] = [int]$Matches[2] }
}
if ($saved.ContainsKey('API_PORT')) { $ApiPort = $saved['API_PORT'] }
if ($saved.ContainsKey('RECEIVER_PORT')) { $ReceiverPort = $saved['RECEIVER_PORT'] }
if ($saved.ContainsKey('BROKER_HEALTH_PORT')) { $BrokerHealthPort = $saved['BROKER_HEALTH_PORT'] }

switch ($Action) {
    'Down' { Invoke-Compose @('down', '--remove-orphans'); return }
    'Reset' {
        # Compose scopes deletion to this explicitly named local project's volume.
        Invoke-Compose @('down', '--volumes', '--remove-orphans')
        # Dotfiles are hidden on Unix; -Force permits removal of this one generated file.
        Remove-Item -LiteralPath $envFile -Force
        return
    }
    'DeadLetters' { Invoke-Compose @('exec', '-T', 'worker', 'dotnet', 'RelayLab.Worker.dll', '--deadletters'); return }
}

try {
    Invoke-Compose @('config', '--quiet')
    Invoke-Compose @('build', 'init-api', 'init-receiver', 'worker')
    # Receiver and broker readiness must precede the M1 single-attempt worker.
    Invoke-Compose @('up', '-d', 'sql', 'servicebus', 'api', 'receiver')
    Wait-Http "http://127.0.0.1:$BrokerHealthPort/health"
    Wait-Http "http://127.0.0.1:$ApiPort/health/ready"
    Wait-Http "http://127.0.0.1:$ReceiverPort/health/ready"
    Invoke-Compose @('up', '-d', '--no-deps', 'worker')

    $key = 'demo-' + [Guid]::NewGuid().ToString('N')
    $body = @{ destinationId = 'demo'; eventType = 'document.ready'; data = @{ documentId = 'doc-001' } } | ConvertTo-Json -Compress
    $accepted = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/events" -Method Post -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $key } -Body $body
    if ($accepted.StatusCode -ne 202) { throw 'Expected durable acceptance (202).' }
    $id = ($accepted.Content | ConvertFrom-Json).deliveryId
    $repeat = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/events" -Method Post -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $key } -Body $body
    if ($repeat.StatusCode -ne 200 -or ($repeat.Content | ConvertFrom-Json).deliveryId -ne $id) { throw 'Idempotent repeat did not return the original delivery.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $status = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/deliveries/$id"
        if ($status.status -eq 'Delivered') { break }
        if ($status.status -eq 'Failed') { throw "Demo delivery failed: $($status.lastOutcome) ($($status.remoteOutcome))." }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($status.status -ne 'Delivered') { throw "Delivery remained $($status.status); inspect the worker and dead-letter queue." }
    $receipt = Invoke-RestMethod -Uri "http://127.0.0.1:$ReceiverPort/receipts/$id"
    if ($receipt.effectCount -ne 1) { throw 'Expected exactly one sample receiver effect.' }
    $evidence = [ordered]@{ deliveryId = $id; status = $status.status; attemptCount = $status.attemptCount; receiverEffectCount = $receipt.effectCount; repeatedSubmission = 200 }
    $directory = Join-Path $root 'artifacts/demo'
    New-Item -ItemType Directory -Force $directory | Out-Null
    $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'result.json') -Encoding utf8
    $evidence | ConvertTo-Json
    Write-Host "API: http://127.0.0.1:$ApiPort  Receiver: http://127.0.0.1:$ReceiverPort"
} catch {
    & docker @compose logs --no-color --tail 100
    throw
}
