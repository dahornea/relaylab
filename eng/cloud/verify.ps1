#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath, [switch]$FailureRecovery)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Cloud $ConfigurationPath
$deployment = Get-Deployment
$api = $deployment.api_url
$receiver = $deployment.receiver_url
$apiAuth = Get-Bearer $deployment.api_audience
$receiverAuth = Get-Bearer $deployment.receiver_audience
$started = [DateTime]::UtcNow.ToString('o')
function Get-Status([string]$Id) { Invoke-RestMethod "$api/deliveries/$Id" -Headers $apiAuth -TimeoutSec 15 }
function Set-ReceiverMode([string]$Mode) {
    $name = "$($Cloud.name_prefix)-receiver"
    $updated = Get-AzureJson @('containerapp','update','--name',$name,'--resource-group',$Cloud.resource_group_name,'--set-env-vars',"Receiver__Mode=$Mode")
    $revision = $updated.properties.latestRevisionName
    Wait-Cloud { (Get-AzureJson @('containerapp','show','--name',$name,'--resource-group',$Cloud.resource_group_name)).properties.latestReadyRevisionName -eq $revision } 'Receiver revision did not become ready.'
}
function Wait-Trace([string]$Id, [string[]]$Required, [string]$FileName) {
    # Child HTTP spans share the trace ID but do not inherit their parent's custom tags.
    $query = "let deliveryTraces = union requests, dependencies | where tostring(customDimensions['relaylab.delivery.id']) == '$Id' | distinct operation_Id; union requests, dependencies | where operation_Id in (deliveryTraces) | summarize names=make_set(name), services=make_set(cloud_RoleName), traces=make_set(operation_Id)"
    Wait-Cloud {
        $telemetry = Get-AzureJson @('monitor','app-insights','query','--app',$deployment.telemetry_app_id,'--analytics-query',$query)
        $rows = @($telemetry.tables[0].rows)
        if (!$rows.Count) { return $false }
        $names = @($rows[0][0] | ConvertFrom-Json)
        $services = @($rows[0][1] | ConvertFrom-Json)
        $traceIds = @($rows[0][2] | ConvertFrom-Json)
        $valid = @($Required | Where-Object { $_ -notin $names }).Count -eq 0 -and @('relaylab-api','relaylab-worker','relaylab-receiver' | Where-Object { $_ -notin $services }).Count -eq 0 -and $traceIds.Count -eq 1
        if ($valid) { $telemetry | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $Evidence $FileName) }
        $valid
    } 'Cloud telemetry did not contain the correlated lifecycle across all three services.' 600
}
Wait-Cloud { try { (Invoke-WebRequest "$api/health/ready" -TimeoutSec 10).StatusCode -eq 200 } catch { $false } } 'API readiness failed.'
foreach ($url in @("$api/deliveries/$([Guid]::NewGuid())", "$receiver/receipts/$([Guid]::NewGuid())", "$receiver/webhooks")) {
    $response = Invoke-WebRequest $url -Method $(if ($url.EndsWith('/webhooks')) {'Post'} else {'Get'}) -SkipHttpErrorCheck -TimeoutSec 15
    if ($response.StatusCode -ne 401) { throw 'Anonymous cloud access was not rejected.' }
}
# The operator token may inspect receipts but must not impersonate the worker at /webhooks.
$forbidden = Invoke-WebRequest "$receiver/webhooks" -Method Post -Headers $receiverAuth -SkipHttpErrorCheck -TimeoutSec 15
if ($forbidden.StatusCode -ne 403) { throw 'Receiver workload access is not correctly separated from diagnostics.' }
$key = 'cloud-' + [Guid]::NewGuid().ToString('N')
$body = @{destinationId='demo'; eventType='document.ready'; data=@{documentId=$key}} | ConvertTo-Json -Compress
$headers = $apiAuth.Clone(); $headers['Idempotency-Key'] = $key
$accepted = Invoke-WebRequest "$api/events" -Method Post -Headers $headers -ContentType application/json -Body $body -TimeoutSec 15
$id = ($accepted.Content | ConvertFrom-Json).deliveryId
if ($accepted.StatusCode -ne 202) { throw 'Fresh cloud event was not accepted.' }
$repeat = Invoke-WebRequest "$api/events" -Method Post -Headers $headers -ContentType application/json -Body $body -TimeoutSec 15
if ($repeat.StatusCode -ne 200 -or ($repeat.Content | ConvertFrom-Json).deliveryId -ne $id) { throw 'Acceptance idempotency failed.' }
Wait-Cloud { (Get-Status $id).status -eq 'Delivered' } 'Cloud delivery did not complete.'
$receipt = Invoke-RestMethod "$receiver/receipts/$id" -Headers $receiverAuth -TimeoutSec 15
if ($receipt.effectCount -ne 1) { throw 'Expected one durable receiver effect.' }
@{ deliveryId=$id; status='Delivered'; receiverEffectCount=1; anonymousRejected=$true; workerAccessSeparated=$true } | ConvertTo-Json | Set-Content (Join-Path $Evidence 'smoke.json')
# Query counters before replacing receiver replicas; buffered metric export is not durable.
$metricQuery = "customMetrics | where timestamp >= datetime($started) | where name in ('relaylab.accepted','relaylab.attempt.started','relaylab.receiver.effects','relaylab.http.duration') | summarize rows=count(), total=sum(valueSum), samples=sum(valueCount) by cloud_RoleName, name"
Wait-Cloud {
    $metrics = Get-AzureJson @('monitor','app-insights','query','--app',$deployment.telemetry_app_id,'--analytics-query',$metricQuery)
    $rows = @($metrics.tables[0].rows)
    foreach ($expected in @(@('relaylab-api','relaylab.accepted'),@('relaylab-worker','relaylab.attempt.started'),@('relaylab-receiver','relaylab.receiver.effects'),@('relaylab-worker','relaylab.http.duration'))) {
        $matching = @($rows | Where-Object { $_[0] -eq $expected[0] -and $_[1] -eq $expected[1] -and $(if ($expected[1] -eq 'relaylab.http.duration') { $_[4] -gt 0 } else { $_[3] -gt 0 }) })
        if (!$matching.Count) { return $false }
    }
    $metrics | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $Evidence 'metrics.json')
    $true
} 'Cloud metric ingestion did not contain the expected service counters and HTTP histogram samples.' 600
if ($FailureRecovery) {
    try {
        Set-ReceiverMode 'Reject'
        $key = 'cloud-failure-' + [Guid]::NewGuid().ToString('N')
        $headers['Idempotency-Key'] = $key
        $body = @{destinationId='demo'; eventType='document.ready'; data=@{documentId=$key}} | ConvertTo-Json -Compress
        $accepted = Invoke-WebRequest "$api/events" -Method Post -Headers $headers -ContentType application/json -Body $body -TimeoutSec 15
        $id = ($accepted.Content | ConvertFrom-Json).deliveryId
        Wait-Cloud { (Get-Status $id).status -eq 'Failed' } 'Cloud retry budget did not exhaust.'
        $failed = Get-Status $id
        if ($failed.attemptCount -ne 3 -or $failed.lastOutcome -ne 'RetryExhausted' -or @($failed.attempts | Where-Object httpStatus -ne 503).Count) { throw 'Unexpected exhausted cloud history.' }
        $failed | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $Evidence 'exhausted.json')
        # Preserve observable telemetry before replacing/restarting buffered exporters.
        Wait-Trace $id @('accept','publish','attempt','webhook','receive','transition') 'pre-restart-telemetry.json'
        Set-ReceiverMode 'CommitThenWaitOnce'
        $replayHeaders = $apiAuth.Clone(); $replayHeaders['Idempotency-Key'] = 'replay-' + [Guid]::NewGuid().ToString('N')
        $replay = Invoke-WebRequest "$api/deliveries/$id/replay" -Method Post -Headers $replayHeaders -TimeoutSec 15
        $again = Invoke-WebRequest "$api/deliveries/$id/replay" -Method Post -Headers $replayHeaders -TimeoutSec 15
        if ($replay.StatusCode -ne 202 -or $again.StatusCode -ne 200 -or ($replay.Content | ConvertFrom-Json).workId -ne ($again.Content | ConvertFrom-Json).workId) { throw 'Replay identity changed.' }
        Wait-Cloud { try { (Invoke-RestMethod "$receiver/receipts/$id" -Headers $receiverAuth -TimeoutSec 15).effectCount -eq 1 } catch { $false } } 'Receiver effect was not committed.'
        $beforeRestart = Get-Status $id
        foreach ($kind in @('worker','receiver')) {
            $app = Get-AzureJson @('containerapp','show','--name',"$($Cloud.name_prefix)-$kind",'--resource-group',$Cloud.resource_group_name)
            Invoke-Native az @('containerapp','revision','restart','--name',"$($Cloud.name_prefix)-$kind",'--resource-group',$Cloud.resource_group_name,'--revision',$app.properties.latestReadyRevisionName,'--only-show-errors')
        }
        Wait-Cloud { (Get-Status $id).status -eq 'Delivered' } 'Cloud replay/restart did not recover.'
        $recovered = Get-Status $id
        $receipt = Invoke-RestMethod "$receiver/receipts/$id" -Headers $receiverAuth -TimeoutSec 15
        if ($recovered.generation -ne 1 -or $recovered.attemptCount -lt 5 -or $recovered.attemptCount -gt 6 -or $receipt.effectCount -ne 1 -or @($recovered.attempts | Where-Object { $_.remoteOutcome -eq 'Unknown' }).Count -lt 1) { throw 'Unexpected recovery history or repeated receiver effect.' }
        @{ deliveryId=$id; beforeRestart=$beforeRestart; recovered=$recovered; receiverEffectCount=$receipt.effectCount; interruption='Azure revision restart; no claim of an exact SIGKILL boundary' } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $Evidence 'recovery.json')
    } finally { Set-ReceiverMode 'Acknowledge' }
}
$required = if ($FailureRecovery) { @('accept','publish','attempt','webhook','receive','transition','replay') } else { @('accept','publish','attempt','webhook','receive','transition') }
Wait-Trace $id $required 'telemetry.json'
Write-Host 'Cloud smoke/recovery and telemetry checks passed.'
