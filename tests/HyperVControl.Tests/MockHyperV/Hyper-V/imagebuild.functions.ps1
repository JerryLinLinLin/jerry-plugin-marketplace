# Image build cmdlets for HyperVControl unit tests: the Storage and DISM cmdlets and the native-tool helper
# (bcdboot, bcdedit, qemu-img) that ImageBuildScripts calls. Dot-sourced by Hyper-V.psm1; later definitions win.
# In mock mode the build scripts skip their Storage/DISM imports and their own Invoke-ImageBuildNative, so these apply.
# State (imageBuild in the state file): isoDriveLetter, diskNumber, driveLetters[], images[], native{ '<tool>.exe' = { exitCode,
# stdout, stderr } }, bcdbootHelp, qemuInfo, failAt (a cmdlet name that throws), createFiles, and the runtime lists
# mountedImages[], attachedVhds[] and partitions[]. The base New-VHD creates no file; with createFiles set, Mount-VHD
# materializes a placeholder so tests can assert that a failed build deletes its VHDX.

function Get-ImageBuildMockState {
    $state = Read-MockState
    if ($null -eq $state.PSObject.Properties['imageBuild']) { $state | Add-Member -NotePropertyName imageBuild -NotePropertyValue ([pscustomobject]@{}) }
    return $state
}

function Set-ImageBuildMockValue($State, [string]$Name, $Value) {
    if ($null -eq $State.imageBuild.PSObject.Properties[$Name]) { $State.imageBuild | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    else { $State.imageBuild.$Name = $Value }
}

function Get-ImageBuildMockList($State, [string]$Name) { return @(@($State.imageBuild.$Name) | Where-Object { $null -ne $_ }) }

function Assert-ImageBuildMockStep([string]$Step) {
    $failAt = [string](Get-ImageBuildMockState).imageBuild.failAt
    if ($failAt -and $failAt -eq $Step) { throw [System.InvalidOperationException]::new("Mock failure injected at $Step.") }
}

function Get-DiskImage {
    [CmdletBinding()] param([Parameter(Position = 0)][string]$ImagePath)
    Write-MockCall 'Get-DiskImage' $PSBoundParameters
    $mounted = @(Get-ImageBuildMockList (Get-ImageBuildMockState) 'mountedImages')
    [pscustomobject]@{ ImagePath = $ImagePath; Attached = ($mounted -contains $ImagePath); StorageType = 1 }
}

function Mount-DiskImage {
    [CmdletBinding()] param([Parameter(Position = 0)][string[]]$ImagePath, [string]$StorageType, [string]$Access, [switch]$PassThru, [switch]$NoDriveLetter)
    Write-MockCall 'Mount-DiskImage' $PSBoundParameters
    Assert-ImageBuildMockStep 'Mount-DiskImage'
    $state = Get-ImageBuildMockState
    Set-ImageBuildMockValue $state 'mountedImages' @(@(Get-ImageBuildMockList $state 'mountedImages') + $ImagePath[0])
    Write-MockState $state
    if ($PassThru) { [pscustomobject]@{ ImagePath = $ImagePath[0]; Attached = $true; StorageType = 1 } }
}

function Dismount-DiskImage {
    [CmdletBinding()] param([Parameter(Position = 0)][string[]]$ImagePath, [string]$DevicePath)
    Write-MockCall 'Dismount-DiskImage' $PSBoundParameters
    $state = Get-ImageBuildMockState
    Set-ImageBuildMockValue $state 'mountedImages' @(Get-ImageBuildMockList $state 'mountedImages' | Where-Object { $_ -ne $ImagePath[0] })
    Write-MockState $state
    [pscustomobject]@{ ImagePath = $ImagePath[0]; Attached = $false }
}

function Get-Volume {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$DiskImage, $Partition, [char[]]$DriveLetter, [string]$FilePath)
    process {
        Write-MockCall 'Get-Volume' $PSBoundParameters
        if ($DiskImage) {
            $letter = [string](Get-ImageBuildMockState).imageBuild.isoDriveLetter
            [pscustomobject]@{ DriveLetter = $(if ($letter) { [char]$letter } else { [char]0 }); FileSystemLabel = 'MOCK_ISO'; FileSystem = 'UDF'; Path = '\\?\Volume{00000000-0000-0000-0000-0000000015f0}\' }
        }
    }
}

function Get-WindowsImage {
    [CmdletBinding()] param([string]$ImagePath, [int]$Index, [string]$Name, [string]$LogPath)
    Write-MockCall 'Get-WindowsImage' $PSBoundParameters
    Assert-ImageBuildMockStep 'Get-WindowsImage'
    $images = @(Get-ImageBuildMockList (Get-ImageBuildMockState) 'images')
    if ($PSBoundParameters.ContainsKey('Index')) {
        $image = @($images | Where-Object { [int]$_.index -eq $Index })[0]
        if (-not $image) { Write-Error -Message "The specified image index $Index does not exist." -Category ObjectNotFound -ErrorAction Stop }
        return [pscustomobject]@{ ImageIndex = [int]$image.index; ImageName = [string]$image.name; ImageDescription = [string]$image.description; ImageSize = [long]$image.size
            EditionId = [string]$image.editionId; Architecture = [uint32]$image.architecture; Version = [string]$image.version; InstallationType = [string]$image.installationType
            Languages = @($image.languages); DefaultLanguageIndex = 0 }
    }
    foreach ($image in $images) { [pscustomobject]@{ ImageIndex = [int]$image.index; ImageName = [string]$image.name; ImageDescription = [string]$image.description; ImageSize = [long]$image.size } }
}

function Expand-WindowsImage {
    [CmdletBinding()] param([string]$ImagePath, [int]$Index, [string]$Name, [string]$ApplyPath, [switch]$CheckIntegrity, [switch]$Verify, [string]$LogPath, [string]$ScratchDirectory)
    Write-MockCall 'Expand-WindowsImage' $PSBoundParameters
    Assert-ImageBuildMockStep 'Expand-WindowsImage'
    [pscustomobject]@{ ImagePath = $ImagePath; Index = $Index }
}

function Mount-VHD {
    [CmdletBinding()] param([Parameter(Position = 0)][string[]]$Path, [switch]$Passthru, [switch]$ReadOnly, [switch]$NoDriveLetter)
    Write-MockCall 'Mount-VHD' $PSBoundParameters
    Assert-ImageBuildMockStep 'Mount-VHD'
    $state = Get-ImageBuildMockState
    $number = if ($state.imageBuild.diskNumber) { [int]$state.imageBuild.diskNumber } else { 7 }
    if ($state.imageBuild.createFiles -and -not (Test-Path -LiteralPath $Path[0])) { [System.IO.File]::WriteAllBytes($Path[0], [byte[]](0x76, 0x68, 0x64, 0x78)) }
    Set-ImageBuildMockValue $state 'attachedVhds' @(@(Get-ImageBuildMockList $state 'attachedVhds') + $Path[0])
    Set-ImageBuildMockValue $state 'partitions' @()
    Write-MockState $state
    if ($Passthru) { [pscustomobject]@{ Path = $Path[0]; DiskNumber = $number; Attached = $true } }
}

function Dismount-VHD {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(Position = 0)][string[]]$Path, [int]$DiskNumber, [switch]$Passthru)
    Write-MockCall 'Dismount-VHD' $PSBoundParameters
    $state = Get-ImageBuildMockState
    $attached = @(Get-ImageBuildMockList $state 'attachedVhds')
    if ($attached -notcontains $Path[0]) { throw [System.InvalidOperationException]::new("The virtual disk '$($Path[0])' is not attached.") }
    Set-ImageBuildMockValue $state 'attachedVhds' @($attached | Where-Object { $_ -ne $Path[0] })
    Write-MockState $state
}

function Initialize-Disk {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(Position = 0)][int]$Number, [string]$PartitionStyle, [switch]$PassThru)
    Write-MockCall 'Initialize-Disk' $PSBoundParameters
    Assert-ImageBuildMockStep 'Initialize-Disk'
    $state = Get-ImageBuildMockState
    # Windows adds a reserved partition to a newly initialized GPT disk; the build must remove it.
    Set-ImageBuildMockValue $state 'partitions' @([pscustomobject]@{ diskNumber = $Number; partitionNumber = 1; size = [long]16759808; gptType = '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'; driveLetter = $null; fileSystem = $null })
    Write-MockState $state
}

function ConvertTo-MockPartition($Row) {
    $type = switch ([string]$Row.gptType) { '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' { 'System' } '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' { 'Reserved' } default { 'Basic' } }
    [pscustomobject]@{ DiskNumber = [int]$Row.diskNumber; PartitionNumber = [int]$Row.partitionNumber; Size = [long]$Row.size; GptType = [string]$Row.gptType; Type = $type
        DriveLetter = $(if ($Row.driveLetter) { [char][string]$Row.driveLetter } else { [char]0 }) }
}

function Get-Partition {
    [CmdletBinding()] param([int]$DiskNumber, [int]$PartitionNumber, [char]$DriveLetter)
    Write-MockCall 'Get-Partition' $PSBoundParameters
    $rows = @(Get-ImageBuildMockList (Get-ImageBuildMockState) 'partitions' | Where-Object { [int]$_.diskNumber -eq $DiskNumber })
    if ($PSBoundParameters.ContainsKey('PartitionNumber')) { $rows = @($rows | Where-Object { [int]$_.partitionNumber -eq $PartitionNumber }) }
    if ($rows.Count -eq 0) { Write-Error -Message "No MSFT_Partition objects found with property 'DiskNumber' equal to '$DiskNumber'." -Category ObjectNotFound; return }
    foreach ($row in $rows) { ConvertTo-MockPartition $row }
}

function Remove-Partition {
    [CmdletBinding(SupportsShouldProcess)] param([int]$DiskNumber, [int]$PartitionNumber)
    Write-MockCall 'Remove-Partition' $PSBoundParameters
    $state = Get-ImageBuildMockState
    Set-ImageBuildMockValue $state 'partitions' @(Get-ImageBuildMockList $state 'partitions' | Where-Object { -not ([int]$_.diskNumber -eq $DiskNumber -and [int]$_.partitionNumber -eq $PartitionNumber) })
    Write-MockState $state
}

function New-Partition {
    [CmdletBinding()] param([int]$DiskNumber, [long]$Size, [switch]$UseMaximumSize, [string]$GptType, [switch]$AssignDriveLetter, [char]$DriveLetter, [long]$Offset)
    Write-MockCall 'New-Partition' $PSBoundParameters
    Assert-ImageBuildMockStep 'New-Partition'
    $state = Get-ImageBuildMockState
    $rows = @(Get-ImageBuildMockList $state 'partitions')
    $number = 1 + [int](@(@($rows | ForEach-Object { [int]$_.partitionNumber }) + 0) | Measure-Object -Maximum).Maximum
    $used = [long](@(@($rows | ForEach-Object { [long]$_.size }) + 0) | Measure-Object -Sum).Sum
    $length = if ($UseMaximumSize) { [long](64GB - 2MB - $used) } else { $Size }
    $row = [pscustomobject]@{ diskNumber = $DiskNumber; partitionNumber = $number; size = $length; driveLetter = $null; fileSystem = $null
        gptType = $(if ($GptType) { $GptType } else { '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' }) }
    Set-ImageBuildMockValue $state 'partitions' @($rows + $row)
    Write-MockState $state
    ConvertTo-MockPartition $row
}

function Format-Volume {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$Partition, [char[]]$DriveLetter, [string]$FileSystem, [string]$NewFileSystemLabel, [switch]$Force, [switch]$Full)
    process {
        Write-MockCall 'Format-Volume' ([ordered]@{ PartitionNumber = [int]$Partition.PartitionNumber; FileSystem = $FileSystem; NewFileSystemLabel = $NewFileSystemLabel; Force = [bool]$Force })
        Assert-ImageBuildMockStep 'Format-Volume'
        $state = Get-ImageBuildMockState
        foreach ($row in Get-ImageBuildMockList $state 'partitions') { if ([int]$row.partitionNumber -eq [int]$Partition.PartitionNumber) { $row.fileSystem = $FileSystem } }
        Write-MockState $state
        [pscustomobject]@{ FileSystem = $FileSystem; FileSystemLabel = $NewFileSystemLabel }
    }
}

function Set-Partition {
    [CmdletBinding(SupportsShouldProcess)] param([int]$DiskNumber, [int]$PartitionNumber, [string]$GptType, [char]$NewDriveLetter, [bool]$IsActive, [bool]$IsHidden)
    Write-MockCall 'Set-Partition' $PSBoundParameters
    $state = Get-ImageBuildMockState
    foreach ($row in Get-ImageBuildMockList $state 'partitions') { if ([int]$row.diskNumber -eq $DiskNumber -and [int]$row.partitionNumber -eq $PartitionNumber -and $GptType) { $row.gptType = $GptType } }
    Write-MockState $state
}

function Add-PartitionAccessPath {
    [CmdletBinding()] param([int]$DiskNumber, [int]$PartitionNumber, [string]$AccessPath, [switch]$AssignDriveLetter, [switch]$PassThru)
    Write-MockCall 'Add-PartitionAccessPath' $PSBoundParameters
    Assert-ImageBuildMockStep 'Add-PartitionAccessPath'
    $state = Get-ImageBuildMockState
    $rows = @(Get-ImageBuildMockList $state 'partitions')
    if ($AssignDriveLetter) {
        $used = @($rows | Where-Object { $_.driveLetter } | ForEach-Object { [string]$_.driveLetter })
        $free = @(Get-ImageBuildMockList $state 'driveLetters' | Where-Object { $used -notcontains [string]$_ })
        foreach ($row in $rows) { if ([int]$row.diskNumber -eq $DiskNumber -and [int]$row.partitionNumber -eq $PartitionNumber -and $free.Count -gt 0) { $row.driveLetter = [string]$free[0] } }
    }
    Write-MockState $state
}

# Replaces the real helper, which ImageBuildScripts defines only outside mock mode. Results come from imageBuild.native;
# qemu-img info/convert are simulated, bcdboot /? advertises /offline unless imageBuild.bcdbootHelp says otherwise.
function Invoke-ImageBuildNative {
    [CmdletBinding()] param([string]$FilePath, [string[]]$ArgumentList)
    Write-MockCall 'Invoke-ImageBuildNative' $PSBoundParameters
    $tool = [System.IO.Path]::GetFileName($FilePath).ToLowerInvariant()
    $config = (Get-ImageBuildMockState).imageBuild
    if ($tool -eq 'bcdboot.exe' -and $ArgumentList -contains '/?') {
        $help = if ($null -ne $config.bcdbootHelp) { [string]$config.bcdbootHelp } else { 'BCDBOOT <source> [/l <locale>] [/s <volume-letter> [/f <firmware type>]] [/v] [/offline]' }
        return @{ exitCode = 87; stdout = $help; stderr = '' }
    }
    if ($tool -like 'qemu-img*') {
        if ($ArgumentList[0] -eq 'info') { return @{ exitCode = 0; stdout = $(if ($config.qemuInfo) { [string]$config.qemuInfo } else { '{ "format": "qcow2", "virtual-size": 3758096384 }' }); stderr = '' } }
        if ($ArgumentList[0] -eq 'convert') { [System.IO.File]::WriteAllBytes($ArgumentList[-1], [byte[]](0x76, 0x68, 0x64, 0x78)); return @{ exitCode = 0; stdout = ''; stderr = '' } }
    }
    $configured = if ($config.native) { $config.native.PSObject.Properties[$tool] } else { $null }
    if ($configured) { return @{ exitCode = [int]$configured.Value.exitCode; stdout = [string]$configured.Value.stdout; stderr = [string]$configured.Value.stderr } }
    if ($tool -eq 'bcdedit.exe' -and $ArgumentList -contains '/enum') {
        return @{ exitCode = 0; stdout = "Windows Boot Loader`n-------------------`nidentifier              {default}`ndebug                   Yes"; stderr = '' }
    }
    return @{ exitCode = 0; stdout = 'The operation completed successfully.'; stderr = '' }
}
