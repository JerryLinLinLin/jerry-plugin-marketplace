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
$version = '0.1.0'
if ((& $exe --version) -ne "Hyper-V Control $version") { throw 'Published version does not match plugin.' }
$sha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path (Join-Path $plugin 'bin') | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $plugin 'bin\HyperVControl.exe') -Force
$manifest = [ordered]@{
    version=$version; rid='win-x64'; framework='net10.0-windows'; selfContained=$true
    sha256=$sha
    url="https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/download/hyper-v-control-v$version/HyperVControl.exe"
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $plugin 'runtime.json') -Encoding UTF8
$release = Join-Path $repo 'build\hyperv-control\release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $release 'HyperVControl.exe') -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $plugin 'THIRD-PARTY-NOTICES.md') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $plugin 'licenses') -Destination $release -Recurse -Force
$zip = Join-Path $release "hyper-v-control-plugin-v$version-win-x64.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
[IO.Compression.ZipFile]::CreateFromDirectory($plugin, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipSha=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
@("$sha  HyperVControl.exe", "$zipSha  $(Split-Path -Leaf $zip)") | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS') -Encoding ASCII
Write-Output "Release artifacts: $release"
