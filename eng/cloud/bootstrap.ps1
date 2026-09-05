#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid]$SubscriptionId,
    [Parameter(Mandatory)][Guid]$TenantId,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9]{5,15}$')][string]$NamePrefix,
    [ValidateSet('Plan','Apply','DestroyPlan','Destroy')][string]$Action = 'Plan'
)
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location -LiteralPath $root
$account = Get-AzureJson @('account','show')
if ($account.id -ne $SubscriptionId.ToString() -or $account.tenantId -ne $TenantId.ToString()) { throw 'Login to the approved tenant/subscription first.' }
New-Item -ItemType Directory -Force artifacts/cloud | Out-Null
$variables = @{ subscription_id=$SubscriptionId.ToString(); tenant_id=$TenantId.ToString(); name_prefix=$NamePrefix; location='westeurope' }
$variables | ConvertTo-Json | Set-Content artifacts/cloud/bootstrap.tfvars.json
$vars = Join-Path $root 'artifacts/cloud/bootstrap.tfvars.json'
Invoke-Native terraform @('-chdir=infra/bootstrap','init','-input=false','-lockfile=readonly')
if ($Action -in @('Plan','Apply')) {
    $plan = Join-Path $root 'artifacts/cloud/bootstrap.tfplan'
    Invoke-Native terraform @('-chdir=infra/bootstrap','plan','-input=false',"-var-file=$vars","-out=$plan")
    if ($Action -eq 'Apply') {
        Invoke-Native terraform @('-chdir=infra/bootstrap','apply','-input=false',$plan)
        & terraform -chdir=infra/bootstrap output -json configuration | Set-Content artifacts/cloud/configuration.json
        if ($LASTEXITCODE -ne 0) { throw 'Bootstrap output failed.' }
    }
} else {
    # Never delete backend storage while application resources still exist.
    $remaining = @(Get-AzureJson @('resource','list','--resource-group',"$NamePrefix-app"))
    if ($remaining.Count) { throw 'Destroy the main application stack before bootstrap resources.' }
    $plan = Join-Path $root 'artifacts/cloud/bootstrap-destroy.tfplan'
    Invoke-Native terraform @('-chdir=infra/bootstrap','plan','-destroy','-input=false',"-var-file=$vars","-out=$plan")
    if ($Action -eq 'Destroy') {
        Invoke-Native terraform @('-chdir=infra/bootstrap','apply','-input=false',$plan)
        foreach ($group in @("$NamePrefix-app","$NamePrefix-control")) {
            $exists = & az group exists --name $group -o tsv
            if ($LASTEXITCODE -ne 0 -or $exists -ne 'false') { throw "Resource group remains: $group" }
        }
        Write-Host 'Both owned resource groups and Terraform-managed bootstrap identities/app registrations removed. Remove the GitHub environment separately.'
    }
}
