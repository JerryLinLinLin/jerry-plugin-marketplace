[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo ('build\launcher-test-' + [guid]::NewGuid().ToString('N'))
$plugin = Join-Path $scratch 'plugin with spaces'
New-Item -ItemType Directory -Force -Path (Join-Path $plugin 'scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'plugins\hyper-v-control\scripts\start.ps1'),(Join-Path $repo 'plugins\hyper-v-control\scripts\install.ps1') -Destination (Join-Path $plugin 'scripts')
foreach ($config in @('.codex-plugin', '.claude-plugin', '.mcp.json')) {
    Copy-Item -LiteralPath (Join-Path $repo "plugins\hyper-v-control\$config") -Destination $plugin -Recurse
}
$runtime = Get-Content -LiteralPath (Join-Path $repo 'plugins\hyper-v-control\runtime.json') -Raw | ConvertFrom-Json
$source = Join-Path $scratch 'LauncherProbe.cs'
$probeSource = @'
using System;
class LauncherProbe {
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--version") {
            Console.WriteLine("Hyper-V Control __VERSION__"); return 0;
        }
        if (args.Length == 1 && args[0] == "--elevate") {
            Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"fixture/started\"}"); return 0;
        }
        return 2;
    }
}
'@
$probeSource.Replace('__VERSION__', $runtime.version) | Set-Content -LiteralPath $source -Encoding UTF8
$probe = Join-Path $scratch 'LauncherProbe.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe ("/out:" + $probe) $source
if ($LASTEXITCODE -ne 0) { throw 'Launcher fixture compilation failed.' }
# A controlled executable exercises setup and stdout routing without UAC or VM operations.
$runtime.sha256 = (Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash.ToLowerInvariant()
$runtime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $plugin 'runtime.json') -Encoding UTF8
$fixture = [pscustomobject]@{url=$runtime.url;exe=$probe;calls=0;corrupt=$false}
Set-Item Function:Invoke-WebRequest -Value ({
    param($Uri,$OutFile,[switch]$UseBasicParsing)
    if ($Uri -ne $fixture.url) { throw 'Launcher requested an unpinned runtime.' }
    $fixture.calls++
    if ($fixture.corrupt) { [IO.File]::WriteAllText($OutFile, 'corrupt download') }
    else { Copy-Item -LiteralPath $fixture.exe -Destination $OutFile }
}.GetNewClosure())
$originalLocalAppData = $env:LOCALAPPDATA
$launcher = Join-Path $plugin 'scripts\start.ps1'
$expected = '{"jsonrpc":"2.0","method":"fixture/started"}'
try {
    $env:LOCALAPPDATA = Join-Path $scratch 'profile'
    $first = @(& $launcher)
    if ($LASTEXITCODE -ne 0 -or $first.Count -ne 1 -or $first[0] -ne $expected -or $fixture.calls -ne 1) {
        throw 'First launch must install once and emit only protocol output.'
    }
    $second = @(& $launcher)
    if ($LASTEXITCODE -ne 0 -or $second.Count -ne 1 -or $second[0] -ne $expected -or $fixture.calls -ne 1) {
        throw 'Second launch must reuse the verified runtime.'
    }
    # Execute each client's actual command after cache relocation, outside the plugin directory.
    Push-Location $scratch
    try {
        foreach ($client in @(
            @{name='Codex';manifest='.codex-plugin/plugin.json';rootVariable='${PLUGIN_ROOT}'},
            @{name='Claude';manifest='.claude-plugin/plugin.json';rootVariable='${CLAUDE_PLUGIN_ROOT}'}
        )) {
            $manifest = Get-Content -LiteralPath (Join-Path $plugin $client.manifest) -Raw | ConvertFrom-Json
            $config = Get-Content -LiteralPath (Join-Path $plugin $manifest.mcpServers) -Raw | ConvertFrom-Json
            $server = $config.mcpServers.'hyper-v-control'
            $clientArgs = @($server.args | ForEach-Object { $_.Replace($client.rootVariable, $plugin.Replace('\', '/')) })
            if ($clientArgs -match '\$\{') { throw "$($client.name) MCP args contain an unresolved variable." }
            $result = @(& $server.command @clientArgs)
            if ($LASTEXITCODE -ne 0 -or $result.Count -ne 1 -or $result[0] -ne $expected) {
                throw "$($client.name) MCP configuration failed after relocation."
            }
        }
    } finally {
        Pop-Location
    }
    $env:LOCALAPPDATA = Join-Path $scratch 'bad-profile'
    $fixture.corrupt = $true
    $rejected = $false
    try { & $launcher | Out-Null }
    catch { if ($_.Exception.Message -like '*SHA-256 verification failed*') { $rejected = $true } else { throw } }
    $installed = Join-Path $env:LOCALAPPDATA "Programs\HyperVControl\$($runtime.version)\HyperVControl.exe"
    if (-not $rejected -or (Test-Path -LiteralPath $installed)) { throw 'Corrupt first-run download was not rejected.' }
    $fixture.corrupt = $false
    $retry = @(& $launcher)
    if ($LASTEXITCODE -ne 0 -or $retry.Count -ne 1 -or $retry[0] -ne $expected) { throw 'Retry after failed download did not recover.' }
} finally {
    $env:LOCALAPPDATA = $originalLocalAppData
}
Write-Output 'PASS: automatic setup, verified reuse, Codex/Claude MCP launch from paths with spaces, clean MCP stdout, corrupt-download rejection, and retry'
