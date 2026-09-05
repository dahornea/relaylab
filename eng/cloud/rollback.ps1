#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath, [Parameter(Mandatory)][string]$ReleasePath)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Cloud $ConfigurationPath
$release = Get-Content -Raw -LiteralPath $ReleasePath | ConvertFrom-Json -AsHashtable
$current = Get-Deployment
if (!$release.verified -or $release.schema_version -ne 1 -or $release.resource_group -ne $Cloud.resource_group_name) { throw 'Rollback requires a previously verified M3 schema-1 release in this environment.' }
Assert-Images $release.images $current.registry_server
if (@('api','worker','receiver' | Where-Object { $release.images[$_] -ne $current.images[$_] }).Count -eq 0) { throw 'Rollback target is already deployed; this would not demonstrate rollback.' }
# Run target schema binaries first. Existing baseline/hash must match; no down migration exists.
$schema = @{api=$release.images.api; receiver=$release.images.receiver}
Apply-Cloud $current.images $true $true $schema
foreach ($kind in @('api','receiver')) {
    $jobName = "$($Cloud.name_prefix)-schema-$kind"
    $execution = Get-AzureJson @('containerapp','job','start','--name',$jobName,'--resource-group',$Cloud.resource_group_name)
    Wait-Cloud {
        $run = @(Get-AzureJson @('containerapp','job','execution','list','--name',$jobName,'--resource-group',$Cloud.resource_group_name)) | Where-Object name -eq $execution.name
        if ($run -and $run.properties.status -in @('Failed','Stopped')) { throw 'Rollback schema compatibility check failed.' }
        $run -and $run.properties.status -eq 'Succeeded'
    } 'Rollback schema check did not complete.' 660
}
Apply-Cloud $release.images $true $true $schema
& (Join-Path $PSScriptRoot 'verify.ps1') -ConfigurationPath $ConfigurationPath
if (!$?) { throw 'Rollback smoke failed.' }
$rollbackRecord = @{ from=$current.images; to=$release.images; schemaVersion=1; databaseRollback=$false; sourceCommit=$release.source_commit; revisions=@{} }
foreach ($kind in @('api','worker','receiver')) {
    $app = Get-AzureJson @('containerapp','show','--name',"$($Cloud.name_prefix)-$kind",'--resource-group',$Cloud.resource_group_name)
    $rollbackRecord.revisions[$kind] = $app.properties.latestReadyRevisionName
}
$rollbackRecord | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Evidence 'rollback.json')
Write-Host 'Application rollback deployed prior immutable images as new Single-mode revisions; SQL was not downgraded.'
