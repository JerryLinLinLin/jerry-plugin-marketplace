#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Repository = 'JerryLinLinLin/jerry-plugin-marketplace',
    [string]$Tag = '',
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Rizin'),
    [switch]$AddToUserPath
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'This runtime requires 64-bit Windows.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$headers = @{ 'User-Agent' = 'rizin-re-toolkit-installer'; 'Accept' = 'application/vnd.github+json' }
$api = "https://api.github.com/repos/$Repository/releases"
if ($Tag) {
    $releases = Invoke-RestMethod -Uri "$api/tags/$([Uri]::EscapeDataString($Tag))" -Headers $headers
} else {
    # Other marketplace plugins may release independently; select by asset.
    $releases = Invoke-RestMethod -Uri "${api}?per_page=100" -Headers $headers
}
$assetPattern = '^rizin-windows-x64-bundle-v[0-9][A-Za-z0-9.-]*\.zip$'
$release = @($releases) | Where-Object {
    -not $_.draft -and -not $_.prerelease -and
    @($_.assets | Where-Object { $_.name -match $assetPattern }).Count -eq 1 -and
    @($_.assets | Where-Object { $_.name -eq 'SHA256SUMS' }).Count -eq 1
} | Select-Object -First 1
if (-not $release) { throw 'No stable Rizin bundle with SHA256SUMS was found. Check the releases page.' }
$asset = $release.assets | Where-Object { $_.name -match $assetPattern }
$checksums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' }
if ($release.tag_name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Unsafe release tag.' }
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$installRoot = Join-Path $destinationRoot $release.tag_name
$exe = Join-Path $installRoot 'rizin\bin\rizin.exe'
if (Test-Path -LiteralPath $installRoot) { throw "Destination exists: $installRoot. Use its rizin\bin\rizin.exe or choose another -Destination." }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('rizin-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $zipPath = Join-Path $stage $asset.name
    $sumPath = Join-Path $stage 'SHA256SUMS'
    Invoke-WebRequest -UseBasicParsing -Uri $checksums.browser_download_url -Headers $headers -OutFile $sumPath
    $lines = @(Get-Content -LiteralPath $sumPath | Where-Object { $_ -match ('^[0-9a-fA-F]{64}\s+\*?' + [Regex]::Escape($asset.name) + '$') })
    if ($lines.Count -ne 1) { throw 'Missing or ambiguous archive checksum.' }
    $expected = ($lines[0] -split '\s+')[0]
    Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -Headers $headers -OutFile $zipPath
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expected) { throw 'SHA-256 verification failed.' }
    if ($asset.digest -and $asset.digest -ne "sha256:$($expected.ToLowerInvariant())") { throw 'GitHub asset digest disagrees with SHA256SUMS.' }
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
    $info = Get-Content -LiteralPath (Join-Path $unpacked 'rizin\bundle-manifest.json') -Raw | ConvertFrom-Json
    $version = & $stagedExe -v 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $version -notmatch ('rizin ' + [Regex]::Escape($info.rizinVersion))) { throw 'Extracted runtime failed its version check.' }
    New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
    if (-not [IO.Path]::GetFullPath($unpacked).StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFullPath($installRoot).StartsWith($destinationRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation paths.' }
    Move-Item -LiteralPath $unpacked -Destination $installRoot
    $bin = Split-Path -Parent $exe
    $env:PATH = "$bin;$env:PATH"
    if ($AddToUserPath) {
        $entries = @([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ })
        if ($entries -notcontains $bin) { [Environment]::SetEnvironmentVariable('Path', (($entries + $bin) -join ';'), 'User') }
    }
    [pscustomobject]@{ Tag = $release.tag_name; Executable = $exe; SHA256 = $expected.ToLowerInvariant(); Version = $version.Trim() }
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $cleanup = [IO.Path]::GetFullPath($stage)
    if ($cleanup.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $cleanup) -match '^rizin-install-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $cleanup)) {
        Remove-Item -LiteralPath $cleanup -Recurse -Force
    }
}
