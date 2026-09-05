#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath, [Parameter(Mandatory)][long]$ReviewerUserId)
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location -LiteralPath $root
if ($ReviewerUserId -le 0) { throw 'Supply the approved GitHub reviewer user ID.' }
$configuration = Get-Content -Raw -LiteralPath $ConfigurationPath
$config = $configuration | ConvertFrom-Json
$body = @{ reviewers=@(@{type='User';id=$ReviewerUserId}); prevent_self_review=$false; deployment_branch_policy=@{protected_branches=$false; custom_branch_policies=$true} }
$body | ConvertTo-Json -Depth 8 | Set-Content artifacts/cloud/github-environment.json
Invoke-Native gh @('api','--method','PUT','repos/dahornea/relaylab/environments/relaylab-demo','--input','artifacts/cloud/github-environment.json')
$existing = & gh api repos/dahornea/relaylab/environments/relaylab-demo/deployment-branch-policies
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect environment branch policies.' }
if (@(($existing | ConvertFrom-Json).branch_policies | Where-Object { $_.name -ne 'main' -or $_.type -ne 'branch' }).Count) { throw 'Unexpected existing branch policies; review rather than overwrite them.' }
if (!(($existing | ConvertFrom-Json).branch_policies | Where-Object name -eq 'main')) {
    @{name='main';type='branch'} | ConvertTo-Json | Set-Content artifacts/cloud/github-branch.json
    Invoke-Native gh @('api','--method','POST','repos/dahornea/relaylab/environments/relaylab-demo/deployment-branch-policies','--input','artifacts/cloud/github-branch.json')
}
# IDs/configuration are nonsecret variables. OIDC is the only workflow login credential.
Invoke-Native gh @('variable','set','M3_CONFIGURATION','--repo','dahornea/relaylab','--env','relaylab-demo','--body',$configuration)
foreach ($entry in @(@('AZURE_CLIENT_ID',$config.deploy_client_id),@('AZURE_TENANT_ID',$config.tenant_id),@('AZURE_SUBSCRIPTION_ID',$config.subscription_id))) {
    Invoke-Native gh @('variable','set',$entry[0],'--repo','dahornea/relaylab','--env','relaylab-demo','--body',$entry[1])
}
