[CmdletBinding()]
param([switch]$AssembleOnly, [int]$Jobs = 8)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'enter-build-env.ps1')
$python = Join-Path $repoRoot 'build\tools\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Create build\tools and install scripts\build-requirements.txt first; see docs/build.md.' }
$buildArgs = @((Join-Path $PSScriptRoot 'build_bundle.py'), '--jobs', $Jobs)
if ($AssembleOnly) { $buildArgs += '--assemble-only' }
& $python @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Bundle build failed: $LASTEXITCODE" }
