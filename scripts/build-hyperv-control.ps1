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
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid runtime version.' }
$tag = "hyper-v-control-v$version"
$exeName = "hyper-v-control-win-x64-v$version.exe"
$noticesName = "$tag-NOTICES.txt"
$manifestName = "$tag-release.json"
$checksumsName = "$tag-SHA256SUMS.txt"
if ((& $exe --version) -ne "Hyper-V Control $version") { throw 'Published version does not match the project.' }
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
$release = Join-Path $repo 'build\hyperv-control\runtime-release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$publicNames = @($exeName, $noticesName, $manifestName, $checksumsName)
if (@(Get-ChildItem -LiteralPath $release -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $publicNames }).Count) {
    throw 'Use a clean runtime-release directory without unrelated or obsolete assets.'
}
Copy-Item -LiteralPath $exe -Destination (Join-Path $release $exeName) -Force
$noticeInputs = @((Join-Path $plugin 'LICENSE'), (Join-Path $plugin 'THIRD-PARTY-NOTICES.md'))
$noticeInputs += @(Get-ChildItem -LiteralPath (Join-Path $plugin 'licenses') -File -Recurse | Sort-Object FullName | Select-Object -ExpandProperty FullName)
$noticeText = [Text.StringBuilder]::new()
foreach ($notice in $noticeInputs) {
    [void]$noticeText.AppendLine("===== $($notice.Substring($plugin.Length + 1).Replace('\', '/')) =====")
    [void]$noticeText.AppendLine([IO.File]::ReadAllText($notice))
}
[IO.File]::WriteAllText((Join-Path $release $noticesName), $noticeText.ToString().Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
$noticesSha = (Get-FileHash -LiteralPath (Join-Path $release $noticesName) -Algorithm SHA256).Hash.ToLowerInvariant()
$assets = @(
    [ordered]@{name=$exeName;kind='executable';rid='win-x64';sha256=$sha;size=(Get-Item $exe).Length},
    [ordered]@{name=$noticesName;kind='notices';sha256=$noticesSha;size=(Get-Item (Join-Path $release $noticesName)).Length}
)
$releaseManifest = [ordered]@{schemaVersion=1;repository='JerryLinLinLin/jerry-plugin-marketplace';plugin='hyper-v-control';version=$version;tag=$tag;assets=$assets}
[IO.File]::WriteAllText((Join-Path $release $manifestName), (($releaseManifest | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$manifestSha = (Get-FileHash -LiteralPath (Join-Path $release $manifestName) -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllLines((Join-Path $release $checksumsName), @("$sha  $exeName", "$noticesSha  $noticesName", "$manifestSha  $manifestName"), [Text.Encoding]::ASCII)
Write-Output "Public assets: $($publicNames -join ', ')"
Write-Output "Release artifacts: $release"
