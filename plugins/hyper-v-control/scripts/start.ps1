#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$runtime = Get-Content -LiteralPath (Join-Path $pluginRoot 'runtime.json') -Raw | ConvertFrom-Json
$exe = Join-Path $pluginRoot 'bin\HyperVControl.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $exe = Join-Path $env:LOCALAPPDATA ('Programs\HyperVControl\' + $runtime.version + '\HyperVControl.exe')
}
if (-not (Test-Path -LiteralPath $exe)) {
    # Capture installer output so MCP stdout contains only protocol messages.
    $ProgressPreference = 'SilentlyContinue'
    $exe = & (Join-Path $PSScriptRoot 'install.ps1')
}
$hashStream = [IO.File]::OpenRead($exe)
$hasher = [Security.Cryptography.SHA256]::Create()
try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($hashStream)).Replace('-', '').ToLowerInvariant() }
finally { $hashStream.Dispose(); $hasher.Dispose() }
if ($actualHash -ne $runtime.sha256) {
    [Console]::Error.WriteLine('Hyper-V Control EXE checksum does not match runtime.json. Reinstall the matching release.')
    exit 1
}
# An elevation-capable EXE bridges its own stdio; ShellExecute never receives MCP stdin.
# Native stdin/out pass directly through this invocation. Diagnostics stay on stderr.
& $exe --elevate
exit $LASTEXITCODE
