[CmdletBinding()]
param([string]$ReleaseDir = '', [string]$ScratchDir = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $repoRoot 'build\release' }
if (-not $ScratchDir) { $ScratchDir = Join-Path $repoRoot ('build\installer-test-' + [Guid]::NewGuid().ToString('N')) }
$rizinFixtureState = [pscustomobject]@{files=(Resolve-Path -LiteralPath $ReleaseDir).Path; corrupt=$false; release=$null; archiveName=''}
$archives = @(Get-ChildItem -LiteralPath $ReleaseDir -Filter 'rizin-windows-x64-bundle-v*.zip')
if ($archives.Count -ne 1) { throw 'Expected exactly one runtime archive' }
$archive = $archives[0]
$rizinFixtureState.archiveName = $archive.Name
$rizinFixtureState.release = [pscustomobject]@{
    tag_name = 'rizin-v0.3.1'; draft = $false; prerelease = $false
    assets = @(
        [pscustomobject]@{name=$archive.Name; browser_download_url="https://fixture.invalid/$($archive.Name)"; digest=('sha256:' + (Get-FileHash $archive.FullName).Hash.ToLowerInvariant())},
        [pscustomobject]@{name='SHA256SUMS'; browser_download_url='https://fixture.invalid/SHA256SUMS'}
    )
}
# Replace only the HTTP boundary. Exercise real checksum, extraction, execution,
# and directory handling in the installer, without publishing an untested asset.
Set-Item Function:Invoke-RestMethod -Value ({
    param($Uri, $Headers)
    if ($Uri -match '/tags/') { return $rizinFixtureState.release }
    # Invoke-RestMethod emits a JSON array as one pipeline object. Include an
    # unrelated newer release to exercise marketplace asset selection too.
    $other = [pscustomobject]@{tag_name='another-plugin-v1.0.0'; draft=$false; prerelease=$false; assets=@()}
    Write-Output -NoEnumerate @($other, $rizinFixtureState.release)
}.GetNewClosure())
Set-Item Function:Invoke-WebRequest -Value ({
    param($Uri, $Headers, $OutFile, [switch]$UseBasicParsing)
    if (-not $Uri.StartsWith('https://fixture.invalid/')) { throw 'Unexpected test URL' }
    $name = ([Uri]$Uri).Segments[-1]
    if ($rizinFixtureState.corrupt -and $name -eq 'SHA256SUMS') {
        (('0' * 64) + '  ' + $rizinFixtureState.archiveName) | Set-Content -LiteralPath $OutFile -Encoding ascii
    } else {
        Copy-Item -LiteralPath (Join-Path $rizinFixtureState.files $name) -Destination $OutFile
    }
}.GetNewClosure())
$installer = Join-Path $repoRoot 'plugins\rizin-windows-re\skills\rizin-windows-re\scripts\install.ps1'
$result = & $installer -Destination (Join-Path $ScratchDir 'valid install')
if (-not (Test-Path -LiteralPath $result.Executable)) { throw 'Installer did not produce an executable' }
$originalHash = (Get-FileHash -LiteralPath $result.Executable).Hash
$refusedExisting = $false
try { & $installer -Destination (Join-Path $ScratchDir 'valid install') | Out-Null }
catch { if ($_.Exception.Message -match 'Destination exists:') { $refusedExisting = $true } else { throw } }
if (-not $refusedExisting -or (Get-FileHash -LiteralPath $result.Executable).Hash -ne $originalHash) { throw 'Existing installation was not preserved' }
$rizinFixtureState.corrupt = $true
$refusedChecksum = $false
try { & $installer -Destination (Join-Path $ScratchDir 'invalid checksum') | Out-Null }
catch { if ($_.Exception.Message -eq 'SHA-256 verification failed.') { $refusedChecksum = $true } else { throw } }
if (-not $refusedChecksum -or (Test-Path -LiteralPath (Join-Path $ScratchDir 'invalid checksum\rizin-v0.3.1'))) { throw 'Checksum failure did not prevent installation' }
[pscustomobject]@{verifiedInstall=$true; existingInstallPreserved=$true; corruptChecksumRejected=$true; executable=$result.Executable} | ConvertTo-Json
