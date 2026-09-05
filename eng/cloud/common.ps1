# Shared helpers; this file alone performs no external operation.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}
function Get-AzureJson([string[]]$Arguments) {
    $raw = & az @Arguments --output json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure command failed.' }
    ($raw | Out-String) | ConvertFrom-Json -AsHashtable
}
function Wait-Cloud([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 300) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Seconds 3
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Initialize-Cloud([string]$ConfigurationPath) {
    $script:Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    Set-Location -LiteralPath $script:Root
    $script:Cloud = Get-Content -Raw -LiteralPath $ConfigurationPath | ConvertFrom-Json -AsHashtable
    foreach ($key in @('subscription_id','tenant_id','deploy_client_id','deploy_object_id','api_audience','receiver_audience')) {
        if (![Guid]::TryParse($script:Cloud[$key], [ref]([Guid]::Empty))) { throw "Invalid configuration UUID: $key" }
    }
    if ($script:Cloud.resource_group_name -ne "$($script:Cloud.name_prefix)-app") { throw 'Unexpected resource group scope.' }
    $account = Get-AzureJson @('account','show')
    if ($account.id -ne $script:Cloud.subscription_id -or $account.tenantId -ne $script:Cloud.tenant_id) { throw 'Azure login does not match the approved subscription/tenant.' }
    $env:ARM_SUBSCRIPTION_ID = $script:Cloud.subscription_id
    $env:ARM_TENANT_ID = $script:Cloud.tenant_id
    if ($env:GITHUB_ACTIONS -eq 'true') { $env:ARM_CLIENT_ID = $script:Cloud.deploy_client_id; $env:ARM_USE_OIDC = 'true' }
    $script:Evidence = Join-Path $script:Root 'artifacts/cloud'
    New-Item -ItemType Directory -Force $script:Evidence | Out-Null
    $vars = @{}
    foreach ($key in @('subscription_id','tenant_id','name_prefix','location','resource_group_name','deploy_object_id','api_audience','receiver_audience')) { $vars[$key] = $script:Cloud[$key] }
    $script:VariablesPath = Join-Path $script:Evidence 'cloud.tfvars.json'
    $vars | ConvertTo-Json | Set-Content -LiteralPath $script:VariablesPath
    Invoke-Native terraform @('-chdir=infra/cloud','init','-input=false','-lockfile=readonly',
        "-backend-config=storage_account_name=$($script:Cloud.state_account)","-backend-config=container_name=$($script:Cloud.state_container)","-backend-config=key=$($script:Cloud.state_key)")
}
function Apply-Cloud([hashtable]$Release, [bool]$Apps, [bool]$Jobs, [hashtable]$SchemaImages) {
    $values = @{ images=$Release; schema_images=$SchemaImages; deploy_apps=$Apps; deploy_schema_jobs=$Jobs }
    $releasePath = Join-Path $script:Evidence 'release.tfvars.json'
    $values | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    $plan = Join-Path $script:Evidence 'deployment.tfplan'
    Invoke-Native terraform @('-chdir=infra/cloud','plan','-input=false',"-var-file=$script:VariablesPath","-var-file=$releasePath","-out=$plan")
    Invoke-Native terraform @('-chdir=infra/cloud','apply','-input=false',$plan)
}
function Get-Deployment {
    $raw = & terraform -chdir=infra/cloud output -json deployment
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read deployment outputs.' }
    ($raw | Out-String) | ConvertFrom-Json -AsHashtable
}
function Assert-Images([hashtable]$Images, [string]$Registry) {
    if ($Images.Count -ne 3) { throw 'A release must contain exactly API, worker and receiver images.' }
    foreach ($kind in @('api','worker','receiver')) {
        if ($Images[$kind] -notmatch ('^'+[regex]::Escape($Registry)+"/relaylab-$kind@sha256:[a-f0-9]{64}$")) { throw "Invalid immutable $kind image." }
    }
}
function Get-Bearer([string]$Audience) {
    $token = & az account get-access-token --resource "api://$Audience" --query accessToken --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or !$token) { throw 'Cannot acquire the verification identity token.' }
    return @{ Authorization = "Bearer $token" } # Never print or persist this object.
}
