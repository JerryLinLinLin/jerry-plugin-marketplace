$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Install the Visual Studio C++ x64 build tools first.' }
$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
& $env:ComSpec /d /s /c "`"$vcvars`" >nul && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the Visual Studio environment.' }
$env:PATH = "$repoRoot\build\tools\Scripts;$env:PATH;C:\Program Files\Git\usr\bin;C:\Program Files\Git\mingw64\bin"
$env:CMAKE_PREFIX_PATH = "$repoRoot\build\rizin"
$env:PKG_CONFIG_PATH = "$repoRoot\build\rizin\lib\pkgconfig"
