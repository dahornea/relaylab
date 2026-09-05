#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$version = '1.7.12'
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'This installer supports amd64 only.' }
$platform = if ($IsWindows) { 'windows_amd64.zip' } elseif ($IsLinux) { 'linux_amd64.tar.gz' } else { throw 'Use Windows or Linux.' }
$directory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/m3/tools/actionlint'
New-Item -ItemType Directory -Force $directory | Out-Null
$name = "actionlint_${version}_$platform"
$base = "https://github.com/rhysd/actionlint/releases/download/v$version"
$archive = Join-Path $directory $name
Invoke-WebRequest "$base/$name" -OutFile $archive
$checksums = (Invoke-WebRequest "$base/actionlint_${version}_checksums.txt").Content
if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
$line = @($checksums -split "`n" | Where-Object { $_.Trim().EndsWith("  $name", [StringComparison]::Ordinal) })
if ($line.Count -ne 1) { throw 'Missing unique official archive checksum.' }
$expected = ($line[0] -split '\s+')[0]
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'actionlint checksum mismatch.' }
if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force }
else {
    & tar -xzf $archive -C $directory
    if ($LASTEXITCODE -ne 0) { throw 'actionlint extraction failed.' }
}
$env:PATH = $directory + [IO.Path]::PathSeparator + $env:PATH
if ($env:GITHUB_PATH) { $directory | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append }
& (Join-Path $directory $(if ($IsWindows) { 'actionlint.exe' } else { 'actionlint' })) -version
if ($LASTEXITCODE -ne 0) { throw 'actionlint installation failed.' }
