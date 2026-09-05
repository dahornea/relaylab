#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath, [Parameter(Mandatory)][string]$ConfirmResourceGroup, [switch]$Apply)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Cloud $ConfigurationPath
if ($ConfirmResourceGroup -ne $Cloud.resource_group_name) { throw 'Teardown resource group does not match the configuration.' }
$plan = Join-Path $Evidence 'teardown.tfplan'
Invoke-Native terraform @('-chdir=infra/cloud','plan','-destroy','-input=false',"-var-file=$VariablesPath","-out=$plan")
if (!$Apply) { Write-Host 'Teardown plan prepared. Apply only under the approved resource scope.'; return }
& terraform -chdir=infra/cloud state pull | Set-Content (Join-Path $Evidence 'pre-teardown-state.json')
if ($LASTEXITCODE -ne 0) { throw 'Could not preserve final state before teardown.' }
Invoke-Native terraform @('-chdir=infra/cloud','apply','-input=false',$plan)
$remaining = @(Get-AzureJson @('resource','list','--resource-group',$ConfirmResourceGroup))
if ($remaining.Count) { throw 'Application resources remain; inspect the inventory before claiming teardown.' }
@{ applicationResourceGroup=$ConfirmResourceGroup; applicationResourcesRemaining=0; bootstrapResourcesRemain=$true; verifiedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $Evidence 'teardown.json')
Write-Host 'Application resources removed. Owner must now destroy bootstrap state resources/identities and remove the GitHub environment as documented.'
