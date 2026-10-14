#Requires -Version 5.1
[CmdletBinding()]
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\HyperVControl'))
$ErrorActionPreference = 'Stop'
function Get-RuntimeHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}
$pluginRoot = Split-Path -Parent $PSScriptRoot
$runtime = Get-Content -LiteralPath (Join-Path $pluginRoot 'runtime.json') -Raw | ConvertFrom-Json
if ($runtime.version -notmatch '^\d+\.\d+\.\d+$' -or $runtime.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid pinned runtime manifest.' }
$root = [IO.Path]::GetFullPath($Destination)
$install = Join-Path $root $runtime.version
$exe = Join-Path $install 'HyperVControl.exe'
if (Test-Path -LiteralPath $exe) {
    if ((Get-RuntimeHash $exe) -eq $runtime.sha256) { Write-Output $exe; return }
    throw 'An existing executable has a different checksum. Choose a fresh destination.'
}
New-Item -ItemType Directory -Force -Path $install | Out-Null
$temporary = Join-Path $install ('download-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $runtime.url -OutFile $temporary
    if ((Get-RuntimeHash $temporary) -ne $runtime.sha256) { throw 'Downloaded EXE SHA-256 verification failed.' }
    Move-Item -LiteralPath $temporary -Destination $exe
    $version = & $exe --version
    if ($LASTEXITCODE -ne 0 -or $version -ne ('Hyper-V Control ' + $runtime.version)) { throw 'Runtime version check failed.' }
    Write-Output $exe
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
