[CmdletBinding()]
param([string]$ReleaseDir = '', [string]$ScratchDir = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ScratchDir) { $ScratchDir = Join-Path $repo ('build\rizin-installer-test-' + [guid]::NewGuid().ToString('N')) }
$plugin = Join-Path $repo 'plugins\rizin-re-toolkit'
$runtime = Get-Content -LiteralPath (Join-Path $plugin 'runtime.json') -Raw | ConvertFrom-Json
$downloads = 'https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/download'

if ($ReleaseDir) {
    # Packaged output (runtimes/rizin/package-bundle.ps1): the real archive and executable are
    # exercised unchanged; the pin is pointed at that archive.
    $release = (Resolve-Path -LiteralPath $ReleaseDir).Path
    $archives = @(Get-ChildItem -LiteralPath $release -Filter 'rizin-windows-x64-bundle-v*.zip')
    if ($archives.Count -ne 1) { throw 'Expected exactly one runtime archive in the release directory.' }
    if ($archives[0].Name -notmatch '-v(\d+\.\d+\.\d+)\.zip$') { throw 'Archive name does not carry a version.' }
    $runtime.tag = "rizin-v$($matches[1])"
    $runtime.asset = $archives[0].Name
    $archive = $archives[0].FullName
} else {
    # Synthetic version probe only: it tests download, hashing, extraction, and installation,
    # not Rizin's analysis engine. The published runtime is checked separately.
    $fixture = Join-Path $ScratchDir 'rizin-fixture'
    $bin = Join-Path $fixture 'rizin\bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    $source = Join-Path $ScratchDir 'VersionProbe.cs'
    @'
using System;
class VersionProbe {
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "-v") { Console.WriteLine("rizin probe"); return 0; }
        return 2;
    }
}
'@ | Set-Content -LiteralPath $source -Encoding UTF8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe ("/out:" + (Join-Path $bin 'rizin.exe')) $source
    if ($LASTEXITCODE -ne 0) { throw 'Version probe compilation failed.' }
    $release = Join-Path $ScratchDir 'rizin-release'
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    $archive = Join-Path $release $runtime.asset
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($fixture, $archive)
}
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

# Stage a plugin copy (path with spaces) whose pin points at the archive under test.
$staged = Join-Path $ScratchDir 'plugin copy'
New-Item -ItemType Directory -Force -Path (Join-Path $staged 'skills\rizin-re-toolkit\scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $plugin 'skills\rizin-re-toolkit\scripts\install.ps1') -Destination (Join-Path $staged 'skills\rizin-re-toolkit\scripts')
$runtime.sha256 = $archiveHash
$runtime.url = "$downloads/$($runtime.tag)/$($runtime.asset)"
$runtime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staged 'runtime.json') -Encoding UTF8
$installer = Join-Path $staged 'skills\rizin-re-toolkit\scripts\install.ps1'

# Replace only the HTTP boundary: the installer may request the pinned URL and nothing else.
$state = [pscustomobject]@{ archive = $archive; url = $runtime.url; corrupt = $false; calls = 0 }
Set-Item Function:Invoke-RestMethod -Value ({ param($Uri) throw "GitHub API must not be used: $Uri" })
Set-Item Function:Invoke-WebRequest -Value ({
    param($Uri, $OutFile, [switch]$UseBasicParsing)
    if ([string]$Uri -ne $state.url) { throw "Installer requested an unpinned URL: $Uri" }
    $state.calls++
    if ($state.corrupt) { [IO.File]::WriteAllText($OutFile, 'corrupt runtime download') }
    else { Copy-Item -LiteralPath $state.archive -Destination $OutFile }
}.GetNewClosure())

# The installer prepends its bin directory to this process's PATH; leave the caller's PATH unchanged.
$pathBefore = $env:PATH
try {
    # 1. Pinned install: one download of the pinned URL, verified, installed under the release tag.
    $result = & $installer -Destination (Join-Path $ScratchDir 'Programs\Rizin')
    if (-not (Test-Path -LiteralPath $result.Executable) -or $state.calls -ne 1) { throw 'Installer did not produce an executable from one download' }
    if ($result.Tag -ne $runtime.tag -or $result.SHA256 -ne $archiveHash -or $result.Version -notmatch '^rizin ') { throw 'Installer result does not describe the pinned runtime' }
    if ((& $result.Executable -v | Out-String) -notmatch '^rizin ') { throw 'Installed executable failed to run' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $ScratchDir 'Programs\Rizin') -Force).Name -ne $runtime.tag) { throw 'Installer left staging files beside the installation' }
    $originalHash = (Get-FileHash -LiteralPath $result.Executable).Hash

    # 2. An existing release directory is preserved and refused before any download.
    $refusedExisting = $false
    try { & $installer -Destination (Join-Path $ScratchDir 'Programs\Rizin') | Out-Null }
    catch { if ($_.Exception.Message -match 'Destination exists:') { $refusedExisting = $true } else { throw } }
    if (-not $refusedExisting -or $state.calls -ne 1 -or (Get-FileHash -LiteralPath $result.Executable).Hash -ne $originalHash) { throw 'Existing installation was not preserved' }

    # 3. A download that does not match the pinned SHA-256 is rejected, leaving nothing behind.
    $state.corrupt = $true
    $refusedArchive = $false
    try { & $installer -Destination (Join-Path $ScratchDir 'corrupt archive') | Out-Null }
    catch { if ($_.Exception.Message -eq 'SHA-256 verification failed.') { $refusedArchive = $true } else { throw } }
    $state.corrupt = $false
    if (-not $refusedArchive -or @(Get-ChildItem -LiteralPath (Join-Path $ScratchDir 'corrupt archive') -Force).Count) { throw 'Corrupt archive was not rejected cleanly' }
} finally {
    $env:PATH = $pathBefore
}

[pscustomobject]@{
    pinnedInstall = $true; singleDownload = $true; existingInstallPreserved = $true; corruptArchiveRejected = $true
    packagedRelease = [bool]$ReleaseDir
    executable = $result.Executable
} | ConvertTo-Json
