#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$logs = Join-Path $artifacts 'verification'
New-Item -ItemType Directory -Force $logs | Out-Null
$originalLocation = Get-Location
$originalDockerHost = $env:DOCKER_HOST

function Invoke-Check([string]$Name, [string]$File, [string[]]$Arguments) {
    & $File @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

try {
    Set-Location -LiteralPath $root
    Invoke-Check 'docker' 'docker' @('info', '--format', '{{.OSType}}')
    $osType = (Get-Content -Raw (Join-Path $logs 'docker.log')).Trim()
    if ($osType -ne 'linux') { throw 'A Docker Linux-container engine is required.' }
    if (!$env:DOCKER_HOST) {
        $endpoint = & docker context inspect --format '{{.Endpoints.docker.Host}}'
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the selected Docker context.' }
        # Docker's Windows context uses npipe:////./pipe; Docker.DotNet.NPipe expects npipe://./pipe.
        $env:DOCKER_HOST = (($endpoint | Out-String).Trim()) -replace '^npipe:////', 'npipe://'
    }
    Invoke-Check 'restore' 'dotnet' @('restore', 'RelayLab.slnx', '--locked-mode', '--configfile', 'NuGet.Config')
    Invoke-Check 'build' 'dotnet' @('build', 'RelayLab.slnx', '-c', 'Release', '--no-restore')
    Invoke-Check 'tests' 'dotnet' @('test', 'tests/RelayLab.Tests/RelayLab.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
        '--logger', 'trx;LogFileName=m1.trx', '--results-directory', (Join-Path $artifacts 'test-results'), '--blame-hang-timeout', '5m')
    # The Docker CLI expects its own Windows URI form; normalization applies only to Testcontainers.
    $env:DOCKER_HOST = $originalDockerHost

    # Git lists tracked plus intended untracked inputs; ignored .env/bin/obj/artifacts never enter the copy.
    $files = & git ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Run verification from the Git checkout so candidate inputs can be enumerated.' }
    $candidate = Join-Path $artifacts ('fresh-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $candidate | Out-Null
    $manifest = foreach ($file in $files) {
        $source = [IO.Path]::GetFullPath((Join-Path $root $file))
        $destination = [IO.Path]::GetFullPath((Join-Path $candidate $file))
        if (!$source.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            !$destination.StartsWith($candidate + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate path escapes workspace.' }
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Candidate input mismatch: $file" }
        "$hash  $file"
    }
    $manifest | Set-Content -LiteralPath (Join-Path $logs 'candidate-inputs.sha256') -Encoding utf8
    $project = 'relaylab-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
    $demo = Join-Path $candidate 'eng/demo.ps1'
    try {
        & $demo -ProjectName $project -ApiPort (Get-FreePort) -ReceiverPort (Get-FreePort) -BrokerHealthPort (Get-FreePort) 2>&1 |
            Tee-Object -FilePath (Join-Path $logs 'fresh-demo.log')
        Copy-Item -LiteralPath (Join-Path $candidate 'artifacts/demo/result.json') -Destination (Join-Path $logs 'fresh-demo-result.json')
    } finally {
        if (Test-Path -LiteralPath (Join-Path $candidate '.env')) {
            & $demo -Action Reset -ProjectName $project 2>&1 | Tee-Object -FilePath (Join-Path $logs 'fresh-demo-cleanup.log')
        }
    }
    Write-Host 'M1 verification passed: locked restore, warning-clean build, SQL/broker tests, and fresh-input container demo.'
} finally {
    $env:DOCKER_HOST = $originalDockerHost
    Set-Location -LiteralPath $originalLocation
}
