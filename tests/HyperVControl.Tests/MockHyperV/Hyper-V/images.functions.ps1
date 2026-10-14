# Image library additions to the recording mock (dot-sourced by Hyper-V.psm1; later definitions win).
# Optional state:
#   host:           { virtualHardDiskPath, virtualMachinePath }  answers Get-VMHost, so tests never touch C:\
#   vhds:           [ { path, parentPath, vhdType } ]            answers Get-VHD, so tests can build differencing chains
#   createVhdFiles: true                                         New-VHD also writes a small placeholder file
#   vms[].snapshots[].disks                                      answers Get-VMHardDiskDrive -VMSnapshot
# New-VHD records every disk it creates in vhds, so a later Get-VHD walk sees the new parent link.

function Get-VMHost {
    [CmdletBinding()] param()
    Write-MockCall 'Get-VMHost' $PSBoundParameters
    $state = Read-MockState
    $vhdPath = 'C:\MockVHDs'
    $vmPath = 'C:\MockVMs'
    if ($state.PSObject.Properties['host'] -and $state.host) {
        if ($state.host.virtualHardDiskPath) { $vhdPath = [string]$state.host.virtualHardDiskPath }
        if ($state.host.virtualMachinePath) { $vmPath = [string]$state.host.virtualMachinePath }
    }
    [pscustomobject]@{ VirtualHardDiskPath = $vhdPath; VirtualMachinePath = $vmPath; EnableEnhancedSessionMode = $true; LogicalProcessorCount = 8; MemoryCapacity = [long]34359738368 }
}

function Get-MockVhdRow([string]$Path) {
    $state = Read-MockState
    if ($null -eq $state.PSObject.Properties['vhds']) { return $null }
    @($state.vhds | Where-Object { $_ -and [string]::Equals([string]$_.path, $Path, [StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
}

function Get-VHD {
    [CmdletBinding()]
    param([Parameter(Position = 0, ValueFromPipeline)][string[]]$Path, [guid[]]$VMId)
    process {
        Write-MockCall 'Get-VHD' $PSBoundParameters
        foreach ($p in $Path) {
            $row = Get-MockVhdRow $p
            $parent = if ($row -and $row.parentPath) { [string]$row.parentPath } else { $null }
            $type = if ($row -and $row.vhdType) { [string]$row.vhdType } elseif ($parent) { 'Differencing' } else { 'Dynamic' }
            [pscustomobject]@{ Path = $p; VhdFormat = 'VHDX'; VhdType = $type; ParentPath = $parent; Size = [long]68719476736; FileSize = [long]4194304; Attached = $false; DiskIdentifier = [guid]::NewGuid().ToString() }
        }
    }
}

function New-VHD {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Path, [long]$SizeBytes, [switch]$Dynamic, [switch]$Fixed, [switch]$Differencing, [string]$ParentPath, [long]$BlockSizeBytes)
    Write-MockCall 'New-VHD' $PSBoundParameters
    $state = Read-MockState
    $type = if ($Differencing) { 'Differencing' } elseif ($Fixed) { 'Fixed' } else { 'Dynamic' }
    $row = [pscustomobject]@{ path = $Path; parentPath = $(if ($Differencing) { $ParentPath } else { $null }); vhdType = $type }
    if ($null -eq $state.PSObject.Properties['vhds']) { $state | Add-Member -NotePropertyName vhds -NotePropertyValue @() }
    $state.vhds = @(@($state.vhds) | Where-Object { $_ -and -not [string]::Equals([string]$_.path, $Path, [StringComparison]::OrdinalIgnoreCase) }) + $row
    Write-MockState $state
    if ($state.PSObject.Properties['createVhdFiles'] -and $state.createVhdFiles -and [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Path))) {
        [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes('vhdxfile mock ' + $type))
    }
    [pscustomobject]@{ Path = $Path; VhdFormat = 'VHDX'; VhdType = $type; ParentPath = $row.parentPath; Size = $SizeBytes; FileSize = [long]4194304; Attached = $false }
}

function Get-VMHardDiskDrive {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, $VMSnapshot, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation)
    process {
        Write-MockCall 'Get-VMHardDiskDrive' $PSBoundParameters
        if ($VMSnapshot) {
            foreach ($snapshot in @($VMSnapshot)) {
                $owner = @((Read-MockState).vms | Where-Object { [string]$_.id -eq [string]$snapshot.VMId }) | Select-Object -First 1
                $row = if ($owner) { @($owner.snapshots | Where-Object { $_ -and [string]$_.id -eq [string]$snapshot.Id }) | Select-Object -First 1 } else { $null }
                $location = 0
                if ($row -and $row.PSObject.Properties['disks']) {
                    foreach ($disk in @($row.disks)) { if ($disk) { [pscustomobject]@{ VMId = $snapshot.VMId; Path = [string]$disk; ControllerType = 'SCSI'; ControllerNumber = 0; ControllerLocation = $location }; $location++ } }
                }
            }
            return
        }
        $row = Get-MockRow $VM
        if ($null -eq $row) { return }
        $location = 0
        foreach ($disk in @($row.disks)) { if ($null -ne $disk) { [pscustomobject]@{ VMId = $VM.Id; Path = [string]$disk; ControllerType = 'SCSI'; ControllerNumber = 0; ControllerLocation = $location }; $location++ } }
    }
}
