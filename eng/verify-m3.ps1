#requires -Version 7.0
[CmdletBinding()]
param([switch]$StaticOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    New-Item -ItemType Directory -Force artifacts/m3 | Out-Null
    $terraform = (Get-Command terraform -ErrorAction Stop).Source
    $actionlint = (Get-Command actionlint -ErrorAction Stop).Source
    $version = (& $terraform version -json | ConvertFrom-Json).terraform_version
    if ($LASTEXITCODE -ne 0 -or $version -ne '1.16.1') { throw 'Use Terraform 1.16.1.' }
    & $terraform fmt -check -recursive infra
    if ($LASTEXITCODE -ne 0) { throw 'Terraform formatting failed.' }
    foreach ($directory in @('infra/bootstrap','infra/cloud')) {
        & $terraform "-chdir=$directory" init -backend=false -input=false -lockfile=readonly -no-color
        if ($LASTEXITCODE -ne 0) { throw 'Terraform provider initialization failed.' }
        & $terraform "-chdir=$directory" validate -no-color
        if ($LASTEXITCODE -ne 0) { throw 'Terraform configuration validation failed.' }
    }
    & $actionlint -color -shellcheck= -pyflakes=
    if ($LASTEXITCODE -ne 0) { throw 'GitHub Actions workflow validation failed.' }
    foreach ($file in Get-ChildItem eng -Filter '*.ps1' -Recurse) {
        $tokens = $null; $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { $errors | ForEach-Object { Write-Error $_ }; throw "PowerShell parsing failed: $($file.Name)" }
    }
    if (!$StaticOnly) {
        & (Join-Path $PSScriptRoot 'verify.ps1')
        if (!$?) { throw 'Complete SQL/broker/container verification failed.' }
    }
    Write-Host "M3 repository verification passed (staticOnly=$StaticOnly). No Azure plan, login, deployment or identity mutation was executed."
} finally { Pop-Location }
