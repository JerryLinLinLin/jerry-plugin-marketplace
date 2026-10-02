[CmdletBinding()]
param([string]$DotNet = 'dotnet', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\HyperVControl\HyperVControl.csproj'
$plugin = Join-Path $repo 'plugins\hyper-v-control'
$out = Join-Path $repo 'build\hyperv-control\publish'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out)
if ($NoRestore) { $arguments += '--no-restore' }
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Hyper-V Control publish failed.' }
$exe = Join-Path $out 'HyperVControl.exe'
$metadata = Get-Content -LiteralPath (Join-Path $plugin 'plugin.json') -Raw | ConvertFrom-Json
$version = $metadata.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid plugin version.' }
$tag = "hyper-v-control-v$version"
$exeName = "hyper-v-control-win-x64-v$version.exe"
$zipName = "hyper-v-control-plugin-v$version-win-x64.zip"
$manifestName = "$tag-release.json"
$checksumsName = "$tag-SHA256SUMS.txt"
if ((& $exe --version) -ne "Hyper-V Control $version") { throw 'Published version does not match plugin.' }
$sha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path (Join-Path $plugin 'bin') | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $plugin 'bin\HyperVControl.exe') -Force
$manifest = [ordered]@{
    plugin='hyper-v-control'; version=$version; tag=$tag; asset=$exeName
    rid='win-x64'; framework='net10.0-windows'; selfContained=$true
    sha256=$sha
    url="https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/download/$tag/$exeName"
}
$manifestJson = ($manifest | ConvertTo-Json).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $plugin 'runtime.json'), $manifestJson, [Text.UTF8Encoding]::new($false))
$release = Join-Path $repo 'build\hyperv-control\release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
# Retain this local convenience path; public assets always use the namespaced name.
Copy-Item -LiteralPath $exe -Destination (Join-Path $release 'HyperVControl.exe') -Force
Copy-Item -LiteralPath $exe -Destination (Join-Path $release $exeName) -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $plugin 'THIRD-PARTY-NOTICES.md') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $plugin 'licenses') -Destination $release -Recurse -Force
$zip = Join-Path $release $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
[IO.Compression.ZipFile]::CreateFromDirectory($plugin, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipSha=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$assets = @(
    [ordered]@{name=$exeName;kind='executable';rid='win-x64';sha256=$sha;size=(Get-Item $exe).Length},
    [ordered]@{name=$zipName;kind='plugin';rid='win-x64';sha256=$zipSha;size=(Get-Item $zip).Length}
)
$releaseManifest = [ordered]@{schemaVersion=1;repository='JerryLinLinLin/jerry-plugin-marketplace';plugin='hyper-v-control';version=$version;tag=$tag;assets=$assets}
[IO.File]::WriteAllText((Join-Path $release $manifestName), (($releaseManifest | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$manifestSha=(Get-FileHash -LiteralPath (Join-Path $release $manifestName) -Algorithm SHA256).Hash.ToLowerInvariant()
$sums=@("$sha  $exeName", "$zipSha  $zipName", "$manifestSha  $manifestName")
[IO.File]::WriteAllLines((Join-Path $release $checksumsName),$sums,[Text.Encoding]::ASCII)
# Local compatibility only. Upload the four namespaced files, never this alias.
@("$sha  HyperVControl.exe", "$zipSha  $zipName") | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS') -Encoding ASCII
Write-Output "Public assets: $exeName, $zipName, $manifestName, $checksumsName"
Write-Output "Release artifacts: $release"
