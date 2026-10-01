[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$RuntimeDir = '',
    [string]$OutputDir = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $RuntimeDir) { $RuntimeDir = Join-Path $repoRoot 'build\portable\rizin' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\release' }
if (-not $Version) { $Version = (Get-Content (Join-Path $repoRoot 'bundle.lock.json') -Raw | ConvertFrom-Json).bundleVersion }
$python = Join-Path $repoRoot 'build\tools\Scripts\python.exe'
& $python (Join-Path $PSScriptRoot 'package_bundle.py') --version $Version --runtime $RuntimeDir --output $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Packaging failed: $LASTEXITCODE" }
