#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Cloud $ConfigurationPath
if (@(git status --porcelain).Count) { throw 'Deploy only a committed clean candidate.' }
$commit = (& git rev-parse HEAD).Trim()
$rawState = & terraform -chdir=infra/cloud show -json
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Terraform state; restore backend access before deployment.' }
$state = ($rawState | Out-String) | ConvertFrom-Json -AsHashtable
$resources = @()
if ($state.ContainsKey('values') -and $state.values.ContainsKey('root_module') -and $state.values.root_module.ContainsKey('resources')) {
    $resources = @($state.values.root_module.resources)
}
$hadApps = @($resources | Where-Object { $_.type -eq 'azurerm_container_app' -and $_.mode -eq 'managed' }).Count -gt 0
# Backend initialization can create an empty blob; an interrupted foundation apply can also
# leave partial resources with no outputs. Converge it before relying on registry outputs.
if (!$hadApps) { Apply-Cloud @{} $false $false @{} }
$prior = Get-Deployment
$prior | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Evidence 'previous-deployment.json')
Invoke-Native az @('acr','login','--name',$prior.registry,'--only-show-errors')
$images = @{}
foreach ($kind in @('api','worker','receiver')) {
    $project = switch ($kind) { api { 'src/RelayLab.Api/RelayLab.Api.csproj' } worker { 'src/RelayLab.Worker/RelayLab.Worker.csproj' } receiver { 'samples/RelayLab.Receiver/RelayLab.Receiver.csproj' } }
    $tag = "$($prior.registry_server)/relaylab-${kind}:$commit"
    Invoke-Native docker @('build','--platform','linux/amd64','--build-arg',"PROJECT=$project",'--label',"org.opencontainers.image.revision=$commit",'-t',$tag,'.')
    Invoke-Native docker @('push',$tag)
    $digest = & az acr repository show --name $prior.registry --image "relaylab-${kind}:$commit" --query digest -o tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Cannot resolve pushed image digest.' }
    $images[$kind] = "$($prior.registry_server)/relaylab-$kind@$digest"
}
Assert-Images $images $prior.registry_server
$schemaImages = @{api=$images.api; receiver=$images.receiver}
if ($hadApps) { Assert-Images $prior.images $prior.registry_server }
# Preserve existing runtime images while the privileged schema jobs are updated and executed.
Apply-Cloud $(if ($hadApps) { $prior.images } else { $images }) $hadApps $true $schemaImages
foreach ($kind in @('api','receiver')) {
    $jobName = "$($Cloud.name_prefix)-schema-$kind"
    $execution = Get-AzureJson @('containerapp','job','start','--name',$jobName,'--resource-group',$Cloud.resource_group_name)
    $executionName = $execution.name
    Wait-Cloud {
        $runs = @(Get-AzureJson @('containerapp','job','execution','list','--name',$jobName,'--resource-group',$Cloud.resource_group_name))
        $run = $runs | Where-Object name -eq $executionName
        if ($run -and $run.properties.status -in @('Failed','Stopped')) { throw "Schema job failed: $jobName/$executionName" }
        $run -and $run.properties.status -eq 'Succeeded'
    } "Schema job did not succeed: $jobName/$executionName" 660
}
Apply-Cloud $images $true $true $schemaImages
$deployment = Get-Deployment
& (Join-Path $PSScriptRoot 'verify.ps1') -ConfigurationPath $ConfigurationPath -FailureRecovery
if (!$?) { throw 'Cloud verification failed.' }
$deployment.source_commit = $commit
$deployment.verified = $true
$deployment.verified_utc = [DateTime]::UtcNow.ToString('o')
$deployment.revisions = @{}
foreach ($kind in @('api','worker','receiver')) {
    $app = Get-AzureJson @('containerapp','show','--name',"$($Cloud.name_prefix)-$kind",'--resource-group',$Cloud.resource_group_name)
    $deployment.revisions[$kind] = $app.properties.latestReadyRevisionName
}
$deployment | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Evidence 'verified-release.json')
