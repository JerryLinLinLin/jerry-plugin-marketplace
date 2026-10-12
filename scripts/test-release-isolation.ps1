[CmdletBinding()]
param([string]$ScratchDir = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ScratchDir) { $ScratchDir = Join-Path $repo ('build\release-isolation-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $ScratchDir | Out-Null
$fixture = Join-Path $ScratchDir 'rizin-fixture'
$fixtureRelease = Join-Path $ScratchDir 'rizin-release'
$bin = Join-Path $fixture 'rizin\bin'
New-Item -ItemType Directory -Force -Path $bin,$fixtureRelease | Out-Null
$source = Join-Path $ScratchDir 'VersionProbe.cs'
@'
using System;
class VersionProbe {
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "-v") { Console.WriteLine("rizin 0.9.1"); return 0; }
        return 2;
    }
}
'@ | Set-Content -LiteralPath $source -Encoding UTF8
# Synthetic version probe only: tests release selection, hashing and installation,
# not Rizin's analysis engine. The published runtime is checked separately.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe ("/out:" + (Join-Path $bin 'rizin.exe')) $source
if ($LASTEXITCODE -ne 0) { throw 'Version probe compilation failed.' }
'{"bundleVersion":"0.3.1","rizinVersion":"0.9.1"}' | Set-Content -LiteralPath (Join-Path $fixture 'rizin\bundle-manifest.json') -Encoding UTF8
$archiveName = 'rizin-windows-x64-bundle-v0.3.1.zip'
$archive = Join-Path $fixtureRelease $archiveName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($fixture,$archive)
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $archiveName" | Set-Content -LiteralPath (Join-Path $fixtureRelease 'SHA256SUMS') -Encoding ASCII
$rizinResult = & (Join-Path $PSScriptRoot 'test-installer.ps1') -ReleaseDir $fixtureRelease -ScratchDir (Join-Path $ScratchDir 'Programs\Rizin') | ConvertFrom-Json
$rizinHash = (Get-FileHash -LiteralPath $rizinResult.executable -Algorithm SHA256).Hash

$plugin = Join-Path $repo 'plugins\hyper-v-control'
$runtime = Get-Content -LiteralPath (Join-Path $plugin 'runtime.json') -Raw | ConvertFrom-Json
$hvcFixture = [pscustomobject]@{url=$runtime.url;exe=(Join-Path $plugin 'bin\HyperVControl.exe');calls=0;corrupt=$false}
if (-not (Test-Path -LiteralPath $hvcFixture.exe)) { throw 'Build Hyper-V Control before running installer tests.' }
Set-Item Function:Invoke-WebRequest -Value ({
    param($Uri,$OutFile,[switch]$UseBasicParsing)
    if ($Uri -ne $hvcFixture.url) { throw 'Installer requested an unpinned or cross-plugin URL.' }
    $hvcFixture.calls++
    if ($hvcFixture.corrupt) { [IO.File]::WriteAllText($OutFile,'corrupt runtime fixture') }
    else { Copy-Item -LiteralPath $hvcFixture.exe -Destination $OutFile }
}.GetNewClosure())
$installer = Join-Path $plugin 'scripts\install.ps1'
$destination = Join-Path $ScratchDir 'Programs\HyperVControl'
$pathBefore = $env:PATH
$installed = & $installer -Destination $destination
if (-not (Test-Path -LiteralPath $installed) -or $hvcFixture.calls -ne 1) { throw 'Pinned Hyper-V installation failed.' }
if ((& $installed --version) -ne "Hyper-V Control $($runtime.version)") { throw 'Installed runtime version mismatch.' }
$second = & $installer -Destination $destination
if ($second -ne $installed -or $hvcFixture.calls -ne 1) { throw 'Matching installation should be reused without downloading.' }
if ($env:PATH -ne $pathBefore -or (Get-FileHash -LiteralPath $rizinResult.executable -Algorithm SHA256).Hash -ne $rizinHash) { throw 'Installing Hyper-V modified PATH or the Rizin installation.' }
$hvcFixture.corrupt = $true
$badDestination = Join-Path $ScratchDir 'bad-hyperv-hash'
$rejected = $false
try { & $installer -Destination $badDestination | Out-Null }
catch { if ($_.Exception.Message -like '*SHA-256 verification failed*') { $rejected=$true } else { throw } }
if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $badDestination "$($runtime.version)\HyperVControl.exe"))) { throw 'Corrupt runtime was not rejected.' }
[pscustomobject]@{
    rizinNamespaceAndPagination=$rizinResult.namespaceIsolation -and $rizinResult.paginationVerified
    pinnedHyperVDownload=$true
    existingHyperVReused=$true
    separateInstallDirectories=$true
    rizinPreserved=$true
    pathPreserved=$true
    corruptHyperVRejected=$true
} | ConvertTo-Json
