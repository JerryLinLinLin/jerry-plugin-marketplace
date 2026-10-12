[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $taskVs) { throw 'MSVC x86/x64 build tools are required only for these native experiments.' }
foreach ($taskArch in 'x64', 'x86') {
    $taskDir = Join-Path $taskOutput $taskArch
    New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
    $taskVcvars = Join-Path $taskVs 'VC\Auxiliary\Build\vcvarsall.bat'
    # Read compiler environment in one shell; no filesystem mutation is delegated.
    $taskVcArch = if ($taskArch -eq 'x86') { 'x64_x86' } else { 'x64' }
    $taskEnvLines = @(& $env:ComSpec /d /s /c "`"$taskVcvars`" $taskVcArch >nul && set")
    if ($LASTEXITCODE -ne 0) { throw 'Compiler environment initialization failed.' }
    $taskNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskLine in $taskEnvLines) {
        if ($taskLine -match '^(PATH|INCLUDE|LIB|LIBPATH|VCToolsInstallDir)=(.*)$') {
            if ($taskNames.Add($matches[1])) { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
        }
    }
    $taskCompiler = Join-Path $env:VCToolsInstallDir "bin\Hostx64\$taskArch\cl.exe"
    Push-Location $taskDir
    try {
        & $taskCompiler /nologo /W3 /Od /Zi /MT /utf-8 (Join-Path $PSScriptRoot 'fixture.c') /Fe:fixture.exe /link /DEBUG
        if ($LASTEXITCODE -ne 0) { throw "Fixture build failed: $taskArch" }
        & $taskCompiler /nologo /W3 /Od /Zi /MT /LD (Join-Path $PSScriptRoot 'late.c') /Fe:late.dll /link /DEBUG
        if ($LASTEXITCODE -ne 0) { throw "DLL build failed: $taskArch" }
    } finally { Pop-Location }
}
