#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Rizin'),
    [switch]$AddToUserPath
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'This runtime requires 64-bit Windows.' }
# The plugin pins the exact runtime it was tested with: one archive, one SHA-256, no GitHub API.
$pluginRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$runtime = Get-Content -LiteralPath (Join-Path $pluginRoot 'runtime.json') -Raw | ConvertFrom-Json
if ($runtime.tag -notmatch '^rizin-v\d+\.\d+\.\d+$' -or $runtime.asset -notmatch '^[A-Za-z0-9._-]+\.zip$' -or
    $runtime.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $runtime.url -notmatch '^https://') { throw 'Invalid pinned runtime manifest.' }
if (-not [IO.Path]::IsPathRooted($Destination)) { $Destination = Join-Path (Get-Location).ProviderPath $Destination }
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$installRoot = Join-Path $destinationRoot $runtime.tag
$exe = Join-Path $installRoot 'rizin\bin\rizin.exe'
if (Test-Path -LiteralPath $installRoot) { throw "Destination exists: $installRoot. Use its rizin\bin\rizin.exe or choose another -Destination." }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# Stage inside the destination root: the final move then never crosses volumes.
New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
$stage = Join-Path $destinationRoot ('.rizin-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $zipPath = Join-Path $stage $runtime.asset
    Invoke-WebRequest -UseBasicParsing -Uri $runtime.url -OutFile $zipPath
    $expected = $runtime.sha256.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'SHA-256 verification failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (-not $name.StartsWith('rizin/') -or $name -match '(^|/)\.\.(/|$)|:') { throw "Unsafe archive member: $name" }
        }
    } finally { $zip.Dispose() }
    $unpacked = Join-Path $stage 'unpacked'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $unpacked
    $stagedExe = Join-Path $unpacked 'rizin\bin\rizin.exe'
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw 'Archive is missing rizin\bin\rizin.exe.' }
    # Windows PowerShell turns redirected stderr lines into errors under 'Stop'; relax it for the probe only.
    $preference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $version = & $stagedExe -v 2>&1 | Out-String } finally { $ErrorActionPreference = $preference }
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^rizin ') { throw 'Extracted runtime failed to run.' }
    Move-Item -LiteralPath $unpacked -Destination $installRoot
    $bin = Split-Path -Parent $exe
    $env:PATH = "$bin;$env:PATH"
    if ($AddToUserPath) {
        $entries = @([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ })
        if ($entries -notcontains $bin) { [Environment]::SetEnvironmentVariable('Path', (($entries + $bin) -join ';'), 'User') }
    }
    [pscustomobject]@{ Tag = $runtime.tag; Executable = $exe; SHA256 = $expected; Version = $version.Trim() }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
