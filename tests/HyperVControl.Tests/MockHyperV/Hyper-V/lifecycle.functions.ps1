# Lifecycle v2 additions to the recording mock, dot-sourced after Hyper-V.psm1's own functions, so the
# definitions here replace the base ones. Virtual disk files are real, empty files under the test's temp
# folder; the state's 'vhds' table carries each file's type, size and parent. Optional state keys:
#   host { virtualMachinePath, virtualHardDiskPath }   failOn [cmdlet names that throw]
#   vhds [{ path, type, parentPath, size, fileSize, mounted }]   nats [{ name, prefix }]   hostAdapters [{ switchName, mac }]
#   per VM: memoryMinimum, memoryMaximum, memoryBuffer, notes, checkpointType, autoCheckpoints, autoStartAction,
#   autoStartDelay, autoStopAction, enhancedSessionTransport, nested, path, secureBoot, template, tpm, keyProtector,
#   tpmInitialized, firstBoot, dvd ('' = empty drive), adapters [{ name, switchName, mac, dynamicMac, spoofing, vlan }],
#   integration { name: enabled }, disks [path | { path, controllerType, controllerNumber, controllerLocation }],
#   snapshots[].disks, mergeDelay (Get-VM calls a merge takes after the last checkpoint is removed).

$script:MockServices = [ordered]@{
    'Guest Service Interface' = '6C09BB55-D683-4DA0-8931-C9BF705F6480'
    'Heartbeat' = '84EAAE65-2F2E-45F5-9BB5-0E857DC8EB47'
    'Key-Value Pair Exchange' = '2A34B1C2-FD73-4043-8A5B-DD2159BC743F'
    'Shutdown' = '9F8233AC-BE49-4C79-8EE3-E7E1985B2077'
    'Time Synchronization' = '2497F4DE-E9FA-4204-80E4-4B75C46419C0'
    'VSS' = '5CED1297-4598-4915-A5FC-AD21BB4D02A4'
}

function Get-MockValue($Row, [string]$Name, $Default) {
    if ($null -ne $Row -and $null -ne $Row.PSObject.Properties[$Name] -and $null -ne $Row.$Name) { return $Row.$Name }
    return $Default
}

function Set-MockProperty($Row, [string]$Name, $Value) {
    if ($null -eq $Row.PSObject.Properties[$Name]) { $Row | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    else { $Row.$Name = $Value }
}

function Stop-Mock([string]$Message, [string]$ErrorId, [System.Management.Automation.ErrorCategory]$Category) {
    Write-Error -Exception ([System.InvalidOperationException]::new($Message)) -ErrorId $ErrorId -Category $Category -ErrorAction Stop
}

function Assert-MockFailure([string]$Cmdlet) {
    $state = Read-MockState
    if ($null -ne $state.PSObject.Properties['failOn'] -and @($state.failOn) -contains $Cmdlet) {
        Stop-Mock "Mock failure injected for $Cmdlet." "OperationFailed,Microsoft.HyperV.PowerShell.Commands.$Cmdlet" InvalidOperation
    }
}

function Assert-MockOff($VM, [string]$Cmdlet) {
    $row = Get-MockRow $VM
    if ($null -ne $row -and [string]$row.state -ne 'Off') {
        Stop-Mock "The operation cannot be performed while the virtual machine is in its current state." "InvalidState,Microsoft.HyperV.PowerShell.Commands.$Cmdlet" InvalidOperation
    }
}

function Get-MockVhdRow($State, [string]$Path) {
    if ($null -eq $State.PSObject.Properties['vhds']) { return $null }
    return @($State.vhds | Where-Object { $null -ne $_ -and [string]$_.path -eq $Path }) | Select-Object -First 1
}

function Add-MockVhdRow($State, $Row) {
    if ($null -eq $State.PSObject.Properties['vhds']) { $State | Add-Member -NotePropertyName vhds -NotePropertyValue @() }
    $State.vhds = @(@($State.vhds | Where-Object { $null -ne $_ -and [string]$_.path -ne [string]$Row.path }) + $Row)
}

function Get-MockDiskPath($Disk) { if ($Disk -is [string]) { return $Disk }; return [string]$Disk.path }

function ConvertTo-MockDrive($Disk, $VmId, [int]$Index) {
    $type = 'SCSI'; $number = 0; $location = $Index
    if ($Disk -isnot [string]) {
        $type = [string](Get-MockValue $Disk 'controllerType' 'SCSI')
        $number = [int](Get-MockValue $Disk 'controllerNumber' 0)
        $location = [int](Get-MockValue $Disk 'controllerLocation' $Index)
    }
    $path = Get-MockDiskPath $Disk
    [pscustomobject]@{ Id = $path; VMId = [guid]$VmId; Path = $path; ControllerType = $type; ControllerNumber = $number; ControllerLocation = $location }
}

function Get-MockAdapters($Row) {
    if ($null -eq $Row.PSObject.Properties['adapters']) { return @([pscustomobject]@{ name = 'Network Adapter'; switchName = 'Default Switch'; mac = '00155D000001'; dynamicMac = $true; spoofing = 'Off'; vlan = 0 }) }
    return @($Row.adapters | Where-Object { $null -ne $_ })
}

function ConvertTo-MockAdapter($Adapter, $VmId) {
    $vlan = [int](Get-MockValue $Adapter 'vlan' 0)
    $switchName = Get-MockValue $Adapter 'switchName' $null
    [pscustomobject]@{
        Id = 'Microsoft:' + [string]$VmId + '\' + [string]$Adapter.name; VMId = [guid]$VmId; Name = [string]$Adapter.name
        SwitchName = $switchName; Connected = (-not [string]::IsNullOrEmpty([string]$switchName))
        MacAddress = [string](Get-MockValue $Adapter 'mac' '000000000000'); DynamicMacAddressEnabled = [bool](Get-MockValue $Adapter 'dynamicMac' $true)
        MacAddressSpoofing = [string](Get-MockValue $Adapter 'spoofing' 'Off')
        VlanSetting = [pscustomobject]@{ OperationMode = $(if ($vlan -gt 0) { 'Access' } else { 'Untagged' }); AccessVlanId = $vlan }
        IPAddresses = @(); Status = @()
    }
}

function Update-MockAdapter($Adapter, [hashtable]$Values) {
    $state = Read-MockState
    foreach ($row in @($state.vms)) {
        if ([string]$row.id -ne [string]$Adapter.VMId) { continue }
        $adapters = @(Get-MockAdapters $row)
        foreach ($entry in $adapters) { if ([string]$entry.name -eq [string]$Adapter.Name) { foreach ($key in $Values.Keys) { Set-MockProperty $entry $key $Values[$key] } } }
        Set-MockProperty $row 'adapters' $adapters
    }
    Write-MockState $state
}

function ConvertTo-MockVm($Row) {
    $status = @('Ok')
    if ([int](Get-MockValue $Row 'mergePolls' 0) -gt 0) { $status = @('Ok', 'MergingDisks') }
    [pscustomobject]@{
        Id = [guid]$Row.id
        Name = [string]$Row.name
        State = [string]$Row.state
        Generation = [int]$Row.generation
        Version = [string](Get-MockValue $Row 'version' '12.0')
        ProcessorCount = [int]$Row.processorCount
        MemoryStartup = [long]$Row.memoryStartup
        MemoryAssigned = [long]0
        MemoryMinimum = [long](Get-MockValue $Row 'memoryMinimum' 536870912)
        MemoryMaximum = [long](Get-MockValue $Row 'memoryMaximum' 1099511627776)
        DynamicMemoryEnabled = [bool]$Row.dynamicMemory
        CheckpointType = [string](Get-MockValue $Row 'checkpointType' 'Production')
        AutomaticCheckpointsEnabled = [bool](Get-MockValue $Row 'autoCheckpoints' $true)
        AutomaticStartAction = [string](Get-MockValue $Row 'autoStartAction' 'StartIfRunning')
        AutomaticStartDelay = [int](Get-MockValue $Row 'autoStartDelay' 0)
        AutomaticStopAction = [string](Get-MockValue $Row 'autoStopAction' 'Save')
        EnhancedSessionTransportType = [string](Get-MockValue $Row 'enhancedSessionTransport' 'VMBus')
        Notes = [string](Get-MockValue $Row 'notes' '')
        ParentSnapshotId = $null
        Path = [string](Get-MockValue $Row 'path' 'C:\MockVMs')
        CreationTime = [datetime]::new(2026, 1, 2, 3, 4, 5, [System.DateTimeKind]::Utc)
        OperationalStatus = $status
        Uptime = [timespan]::Zero
        GuestOperatingSystem = $null
    }
}

# Hyper-V merges checkpoint files asynchronously: each Get-VM call counts a pending merge down, and at zero
# every drive moves to its first non-.avhdx ancestor and the merged .avhdx files disappear.
function Step-MockMerge {
    $state = Read-MockState
    $changed = $false
    foreach ($row in @($state.vms)) {
        $polls = [int](Get-MockValue $row 'mergePolls' 0)
        if ($polls -le 0) { continue }
        $changed = $true
        $polls--
        $row.mergePolls = $polls
        if ($polls -gt 0) { continue }
        $disks = @()
        foreach ($disk in @($row.disks | Where-Object { $null -ne $_ })) {
            $path = Get-MockDiskPath $disk
            while ($path -match '\.avhdx?$') {
                $parent = [string](Get-MockValue (Get-MockVhdRow $state $path) 'parentPath' '')
                if ([System.IO.File]::Exists($path)) { [System.IO.File]::Delete($path) }
                $state.vhds = @($state.vhds | Where-Object { $null -ne $_ -and [string]$_.path -ne $path })
                if (-not $parent) { break }
                $path = $parent
            }
            if ($disk -is [string]) { $disks += $path } else { $disk.path = $path; $disks += $disk }
        }
        $row.disks = $disks
    }
    if ($changed) { Write-MockState $state }
}

function Get-VM {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string[]]$Name, [guid[]]$Id)
    Write-MockCall 'Get-VM' $PSBoundParameters
    Step-MockMerge
    $rows = @((Read-MockState).vms)
    if ($PSBoundParameters.ContainsKey('Id')) {
        foreach ($one in $Id) {
            $row = @($rows | Where-Object { [string]$_.id -eq [string]$one })
            if ($row.Count -eq 0) {
                $exception = [System.Management.Automation.ItemNotFoundException]::new("Hyper-V was unable to find a virtual machine with ID $one.")
                Write-Error -Exception $exception -ErrorId 'InvalidParameter,Microsoft.HyperV.PowerShell.Commands.GetVM' -Category ObjectNotFound -ErrorAction Stop
            }
            ConvertTo-MockVm $row[0]
        }
        return
    }
    if ($PSBoundParameters.ContainsKey('Name')) {
        $patterns = $Name
        $rows = @($rows | Where-Object { $candidate = [string]$_.name; @($patterns | Where-Object { $candidate -like $_ }).Count -gt 0 })
    }
    foreach ($row in $rows) { ConvertTo-MockVm $row }
}

function Get-VMHost {
    [CmdletBinding()] param()
    Write-MockCall 'Get-VMHost' $PSBoundParameters
    $hostRow = Get-MockValue (Read-MockState) 'host' $null
    [pscustomobject]@{
        VirtualHardDiskPath = [string](Get-MockValue $hostRow 'virtualHardDiskPath' 'C:\MockVHDs')
        VirtualMachinePath = [string](Get-MockValue $hostRow 'virtualMachinePath' 'C:\MockVMs')
        EnableEnhancedSessionMode = $true; LogicalProcessorCount = 8; MemoryCapacity = [long]34359738368
        SecureBootTemplates = @([pscustomobject]@{ Name = 'MicrosoftWindows' }, [pscustomobject]@{ Name = 'MicrosoftUEFICertificateAuthority' }, [pscustomobject]@{ Name = 'OpenSourceShieldedVM' })
    }
}

function New-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Name, [string]$Path, [int]$Generation = 1, [long]$MemoryStartupBytes = 536870912,
        [string]$VHDPath, [string]$NewVHDPath, [long]$NewVHDSizeBytes, [string]$SwitchName, [switch]$NoVHD, [string]$Version, [string]$BootDevice, [switch]$Force)
    Write-MockCall 'New-VM' $PSBoundParameters
    Assert-MockFailure 'New-VM'
    $state = Read-MockState
    if ($NewVHDPath) {
        if ([System.IO.File]::Exists($NewVHDPath)) { Stop-Mock 'The file exists. (0x80070050)' 'CreateVHD,Microsoft.HyperV.PowerShell.Commands.NewVM' ResourceExists }
        if (-not [System.IO.Directory]::Exists([System.IO.Path]::GetDirectoryName($NewVHDPath))) { Stop-Mock 'The system cannot find the path specified.' 'ObjectNotFound,Microsoft.HyperV.PowerShell.Commands.NewVM' ObjectNotFound }
        [System.IO.File]::WriteAllBytes($NewVHDPath, [byte[]]@())
        Add-MockVhdRow $state ([pscustomobject]@{ path = $NewVHDPath; type = 'Dynamic'; parentPath = $null; size = $NewVHDSizeBytes })
    }
    $disks = @()
    if ($VHDPath) { $disks += $VHDPath } elseif ($NewVHDPath) { $disks += $NewVHDPath }
    $id = [guid]::NewGuid().ToString()
    $vmPath = 'C:\MockVMs'
    if ($Path) {
        $vmPath = [System.IO.Path]::Combine($Path, $Name)
        $configuration = [System.IO.Path]::Combine($vmPath, 'Virtual Machines')
        [void][System.IO.Directory]::CreateDirectory($configuration)
        [System.IO.File]::WriteAllBytes([System.IO.Path]::Combine($configuration, $id.ToUpperInvariant() + '.vmcx'), [byte[]]@())
    }
    $adapter = [pscustomobject]@{ name = 'Network Adapter'; switchName = $(if ($SwitchName) { $SwitchName } else { $null }); mac = '000000000000'; dynamicMac = $true; spoofing = 'Off'; vlan = 0 }
    # Like New-VM on Windows 11 26200 (observed live): dynamic memory is on (512 MB minimum, 1 TB maximum)
    # and the network adapter boots ahead of the disk.
    $row = [pscustomobject]@{
        id = $id; name = $Name; state = 'Off'; generation = $Generation; processorCount = 1; memoryStartup = $MemoryStartupBytes; dynamicMemory = $true
        memoryMinimum = [long]536870912; memoryMaximum = [long]1099511627776; firstBoot = 'network'
        disks = $disks; snapshots = @(); path = $vmPath; adapters = @($adapter); secureBoot = 'On'; template = 'MicrosoftWindows'; tpm = $false; keyProtector = $false
        checkpointType = 'Production'; autoCheckpoints = $true
    }
    if ($Generation -eq 1) { $row | Add-Member -NotePropertyName dvd -NotePropertyValue '' }
    $state.vms = @($state.vms) + $row
    Write-MockState $state
    ConvertTo-MockVm $row
}

function Remove-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Force)
    process {
        Write-MockCall 'Remove-VM' $PSBoundParameters
        Assert-MockFailure 'Remove-VM'
        if ($VM) {
            $row = Get-MockRow $VM
            if ($null -ne $row -and @('Off', 'Saved') -notcontains [string]$row.state) { Stop-Mock 'The operation cannot be performed while the object is in its current state.' 'InvalidState,Microsoft.HyperV.PowerShell.Commands.RemoveVM' InvalidOperation }
            $configuration = [System.IO.Path]::Combine([string]$VM.Path, 'Virtual Machines', ([string]$VM.Id).ToUpperInvariant() + '.vmcx')
            if ([System.IO.File]::Exists($configuration)) { [System.IO.File]::Delete($configuration) }
            Remove-MockVm $VM
        }
    }
}

function Set-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [long]$ProcessorCount, [long]$MemoryStartupBytes, [long]$MemoryMinimumBytes, [long]$MemoryMaximumBytes,
        [switch]$DynamicMemory, [switch]$StaticMemory, [string]$CheckpointType, [bool]$AutomaticCheckpointsEnabled, [string]$Notes,
        [string]$AutomaticStartAction, [int]$AutomaticStartDelay, [string]$AutomaticStopAction, [string]$NewVMName, [string]$EnhancedSessionTransportType,
        [string]$SnapshotFileLocation, [string]$SmartPagingFilePath, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VM' $PSBoundParameters
        Assert-MockFailure 'Set-VM'
        if ($VM) {
            $map = @{ ProcessorCount = 'processorCount'; MemoryStartupBytes = 'memoryStartup'; CheckpointType = 'checkpointType'; AutomaticCheckpointsEnabled = 'autoCheckpoints'
                Notes = 'notes'; AutomaticStartAction = 'autoStartAction'; AutomaticStartDelay = 'autoStartDelay'; AutomaticStopAction = 'autoStopAction'
                NewVMName = 'name'; EnhancedSessionTransportType = 'enhancedSessionTransport' }
            foreach ($key in $map.Keys) { if ($PSBoundParameters.ContainsKey($key)) { Update-MockVm $VM $map[$key] $PSBoundParameters[$key] } }
            if ($DynamicMemory) { Update-MockVm $VM 'dynamicMemory' $true }
            if ($StaticMemory) { Update-MockVm $VM 'dynamicMemory' $false }
        }
    }
}

function Get-VMMemory {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process {
        Write-MockCall 'Get-VMMemory' $PSBoundParameters
        $row = Get-MockRow $VM
        [pscustomobject]@{ VMId = $VM.Id; Startup = [long]$row.memoryStartup; Minimum = [long](Get-MockValue $row 'memoryMinimum' 536870912)
            Maximum = [long](Get-MockValue $row 'memoryMaximum' 1099511627776); DynamicMemoryEnabled = [bool]$row.dynamicMemory
            Buffer = [int](Get-MockValue $row 'memoryBuffer' 20); Priority = 50 }
    }
}

function Set-VMMemory {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [long]$StartupBytes, [long]$MinimumBytes, [long]$MaximumBytes, [bool]$DynamicMemoryEnabled, [int]$Buffer, [int]$Priority, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMMemory' $PSBoundParameters
        Assert-MockFailure 'Set-VMMemory'
        if ($VM) {
            $map = @{ StartupBytes = 'memoryStartup'; MinimumBytes = 'memoryMinimum'; MaximumBytes = 'memoryMaximum'; DynamicMemoryEnabled = 'dynamicMemory'; Buffer = 'memoryBuffer' }
            foreach ($key in $map.Keys) { if ($PSBoundParameters.ContainsKey($key)) { Update-MockVm $VM $map[$key] $PSBoundParameters[$key] } }
        }
    }
}

function Get-VMProcessor {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process {
        Write-MockCall 'Get-VMProcessor' $PSBoundParameters
        $row = Get-MockRow $VM
        [pscustomobject]@{ VMId = $VM.Id; Count = [int]$row.processorCount; Reserve = [long]0; Maximum = [long]100; RelativeWeight = 100; ExposeVirtualizationExtensions = [bool](Get-MockValue $row 'nested' $false) }
    }
}

function Set-VMProcessor {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [long]$Count, [long]$Reserve, [long]$Maximum, [int]$RelativeWeight, [bool]$ExposeVirtualizationExtensions, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMProcessor' $PSBoundParameters
        Assert-MockFailure 'Set-VMProcessor'
        if ($VM -and $PSBoundParameters.ContainsKey('Count')) { Update-MockVm $VM 'processorCount' $Count }
        if ($VM -and $PSBoundParameters.ContainsKey('ExposeVirtualizationExtensions')) { Update-MockVm $VM 'nested' $ExposeVirtualizationExtensions }
    }
}

function Get-VMHardDiskDrive {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, $VMSnapshot, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation)
    process {
        Write-MockCall 'Get-VMHardDiskDrive' $PSBoundParameters
        $disks = @(); $vmId = $null
        if ($VMSnapshot) {
            $row = @((Read-MockState).vms | Where-Object { [string]$_.id -eq [string]$VMSnapshot.VMId }) | Select-Object -First 1
            $snapshot = @($row.snapshots | Where-Object { $null -ne $_ -and [string]$_.id -eq [string]$VMSnapshot.Id }) | Select-Object -First 1
            $disks = @(Get-MockValue $snapshot 'disks' @()); $vmId = $row.id
        } else {
            $row = Get-MockRow $VM
            if ($null -eq $row) { return }
            $disks = @($row.disks); $vmId = $row.id
        }
        $index = 0
        foreach ($disk in $disks) { if ($null -ne $disk) { ConvertTo-MockDrive $disk $vmId $index; $index++ } }
    }
}

function Add-VMHardDiskDrive {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation, [string]$Path, [switch]$Passthru)
    process {
        Write-MockCall 'Add-VMHardDiskDrive' $PSBoundParameters
        Assert-MockFailure 'Add-VMHardDiskDrive'
        if ($VM) {
            $row = Get-MockRow $VM
            $disks = @(); if ($row -and $row.disks) { $disks = @($row.disks | Where-Object { $null -ne $_ }) }
            $entry = [pscustomobject]@{ path = $Path; controllerType = $(if ($ControllerType) { $ControllerType } else { 'SCSI' }); controllerNumber = $ControllerNumber
                controllerLocation = $(if ($PSBoundParameters.ContainsKey('ControllerLocation')) { $ControllerLocation } else { $disks.Count }) }
            Update-MockVm $VM 'disks' (@($disks) + $entry)
        }
    }
}

function Remove-VMHardDiskDrive {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMHardDiskDrive, [string]$VMName, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation, [switch]$Passthru)
    process {
        Write-MockCall 'Remove-VMHardDiskDrive' $PSBoundParameters
        if ($VMHardDiskDrive) {
            $state = Read-MockState
            foreach ($row in @($state.vms)) {
                if ([string]$row.id -eq [string]$VMHardDiskDrive.VMId) { $row.disks = @($row.disks | Where-Object { $null -ne $_ -and (Get-MockDiskPath $_) -ne [string]$VMHardDiskDrive.Path }) }
            }
            Write-MockState $state
        }
    }
}

function Get-VMDvdDrive {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [int]$ControllerNumber, [int]$ControllerLocation)
    process {
        Write-MockCall 'Get-VMDvdDrive' $PSBoundParameters
        $row = Get-MockRow $VM
        if ($null -ne $row -and $null -ne $row.PSObject.Properties['dvd'] -and $null -ne $row.dvd) {
            [pscustomobject]@{ Id = 'DVD'; VMId = $VM.Id; Name = 'DVD Drive'; Path = $(if ($row.dvd) { [string]$row.dvd } else { $null }); ControllerNumber = 0; ControllerLocation = 1 }
        }
    }
}

function Add-VMDvdDrive {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Path, [int]$ControllerNumber, [int]$ControllerLocation, [switch]$Passthru)
    process {
        Write-MockCall 'Add-VMDvdDrive' $PSBoundParameters
        Assert-MockFailure 'Add-VMDvdDrive'
        if ($VM) {
            Update-MockVm $VM 'dvd' $(if ($Path) { $Path } else { '' })
            if ($Passthru) { [pscustomobject]@{ Id = 'DVD'; VMId = $VM.Id; Name = 'DVD Drive'; Path = $Path; ControllerNumber = 0; ControllerLocation = 1 } }
        }
    }
}

function Set-VMDvdDrive {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMDvdDrive, [string[]]$VMName, [string]$Path, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMDvdDrive' $PSBoundParameters
        if ($VMDvdDrive) {
            $state = Read-MockState
            foreach ($row in @($state.vms)) { if ([string]$row.id -eq [string]$VMDvdDrive.VMId) { Set-MockProperty $row 'dvd' $(if ($Path) { $Path } else { '' }) } }
            Write-MockState $state
        }
    }
}

function Get-VMFirmware {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process {
        Write-MockCall 'Get-VMFirmware' $PSBoundParameters
        $row = Get-MockRow $VM
        $drives = @(); $dvd = @(); $network = @(); $index = 0
        foreach ($disk in @($row.disks | Where-Object { $null -ne $_ })) { $drives += [pscustomobject]@{ BootType = 'Drive'; Description = 'SCSI Disk'; Device = (ConvertTo-MockDrive $disk $row.id $index); FirmwarePath = '' }; $index++ }
        if ($null -ne $row.PSObject.Properties['dvd'] -and $null -ne $row.dvd) { $dvd += [pscustomobject]@{ BootType = 'Drive'; Description = 'SCSI DVD'; Device = [pscustomobject]@{ Id = 'DVD'; Name = 'DVD Drive'; Path = [string]$row.dvd }; FirmwarePath = '' } }
        foreach ($adapter in Get-MockAdapters $row) { $network += [pscustomobject]@{ BootType = 'Network'; Description = 'Network Adapter'; Device = (ConvertTo-MockAdapter $adapter $row.id); FirmwarePath = '' } }
        $first = [string](Get-MockValue $row 'firstBoot' 'disk')
        $order = if ($first -eq 'dvd') { @($dvd) + @($drives) + @($network) } elseif ($first -eq 'network') { @($network) + @($drives) + @($dvd) } else { @($drives) + @($dvd) + @($network) }
        [pscustomobject]@{ VMId = $VM.Id; SecureBoot = [string](Get-MockValue $row 'secureBoot' 'On'); SecureBootTemplate = [string](Get-MockValue $row 'template' 'MicrosoftWindows')
            PreferredNetworkBootProtocol = 'IPv4'; BootOrder = $order }
    }
}

function Set-VMFirmware {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$EnableSecureBoot, [string]$SecureBootTemplate, $FirstBootDevice, $BootOrder, [string]$PreferredNetworkBootProtocol, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMFirmware' $PSBoundParameters
        Assert-MockFailure 'Set-VMFirmware'
        if ($VM) {
            Assert-MockOff $VM 'SetVMFirmware'
            $row = Get-MockRow $VM
            if ($PSBoundParameters.ContainsKey('SecureBootTemplate')) {
                if ([bool](Get-MockValue $row 'tpmInitialized' $false)) { Stop-Mock 'Cannot modify the secure boot template ID property after the virtual TPM is initialized.' 'OperationFailed,Microsoft.HyperV.PowerShell.Commands.SetVMFirmware' InvalidOperation }
                Update-MockVm $VM 'template' $SecureBootTemplate
            }
            if ($PSBoundParameters.ContainsKey('EnableSecureBoot')) { Update-MockVm $VM 'secureBoot' $EnableSecureBoot }
            if ($PSBoundParameters.ContainsKey('FirstBootDevice')) {
                $kind = if ([string]$FirstBootDevice.Id -eq 'DVD') { 'dvd' } elseif ($null -ne $FirstBootDevice.PSObject.Properties['ControllerType']) { 'disk' } else { 'network' }
                Update-MockVm $VM 'firstBoot' $kind
            }
        }
    }
}

function Get-VMSecurity {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process {
        Write-MockCall 'Get-VMSecurity' $PSBoundParameters
        $row = Get-MockRow $VM
        [pscustomobject]@{ VMId = $VM.Id; TpmEnabled = [bool](Get-MockValue $row 'tpm' $false); KsdEnabled = $false; Shielded = $false; BindToHostTpm = $false
            EncryptStateAndVmMigrationTraffic = $false; VirtualizationBasedSecurityOptOut = $false }
    }
}

function Get-VMKeyProtector {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process {
        Write-MockCall 'Get-VMKeyProtector' $PSBoundParameters
        if ([bool](Get-MockValue (Get-MockRow $VM) 'keyProtector' $false)) { , ([byte[]](1..200)) } else { , ([byte[]](0, 0, 0, 4)) }
    }
}

function Set-VMKeyProtector {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$NewLocalKeyProtector, [switch]$RestoreLastKnownGoodKeyProtector, [byte[]]$KeyProtector, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMKeyProtector' $PSBoundParameters
        Assert-MockFailure 'Set-VMKeyProtector'
        if ($VM) { Assert-MockOff $VM 'SetVMKeyProtector'; if ($NewLocalKeyProtector) { Update-MockVm $VM 'keyProtector' $true } }
    }
}

function Enable-VMTPM {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$Passthru)
    process {
        Write-MockCall 'Enable-VMTPM' $PSBoundParameters
        Assert-MockFailure 'Enable-VMTPM'
        if ($VM) {
            Assert-MockOff $VM 'EnableVMTPM'
            if (-not [bool](Get-MockValue (Get-MockRow $VM) 'keyProtector' $false)) { Stop-Mock 'Unable to change the selected security settings for a virtual machine without a valid key protector configured.' 'OperationFailed,Microsoft.HyperV.PowerShell.Commands.EnableVMTPM' InvalidOperation }
            Update-MockVm $VM 'tpm' $true
        }
    }
}

function Disable-VMTPM {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$Passthru)
    process { Write-MockCall 'Disable-VMTPM' $PSBoundParameters; if ($VM) { Assert-MockOff $VM 'DisableVMTPM'; Update-MockVm $VM 'tpm' $false } }
}

function Get-VMBios {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMBios' $PSBoundParameters; [pscustomobject]@{ VMId = $VM.Id; StartupOrder = @('CD', 'IDE', 'LegacyNetworkAdapter', 'Floppy'); NumLockEnabled = $false } }
}

function Get-VMIntegrationService {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name)
    process {
        Write-MockCall 'Get-VMIntegrationService' $PSBoundParameters
        $overrides = Get-MockValue (Get-MockRow $VM) 'integration' $null
        foreach ($service in $script:MockServices.Keys) {
            $enabled = [bool](Get-MockValue $overrides $service ($service -ne 'Guest Service Interface'))
            [pscustomobject]@{ VMId = $VM.Id; Id = 'Microsoft:' + [string]$VM.Id + '\' + $script:MockServices[$service]; Name = $service; Enabled = $enabled; PrimaryStatusDescription = $null }
        }
    }
}

function Set-MockIntegration($VM, [string[]]$Names, [bool]$Enabled) {
    $state = Read-MockState
    foreach ($row in @($state.vms)) {
        if ([string]$row.id -ne [string]$VM.Id) { continue }
        $map = Get-MockValue $row 'integration' $null
        if ($null -eq $map) { $map = [pscustomobject]@{}; Set-MockProperty $row 'integration' $map }
        foreach ($name in $Names) { Set-MockProperty $map $name $Enabled }
    }
    Write-MockState $state
}

function Enable-VMIntegrationService {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Enable-VMIntegrationService' $PSBoundParameters; if ($VM) { Set-MockIntegration $VM $Name $true } }
}

function Disable-VMIntegrationService {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Disable-VMIntegrationService' $PSBoundParameters; if ($VM) { Set-MockIntegration $VM $Name $false } }
}

function Get-VMNetworkAdapter {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [Parameter(Position = 0)][string]$Name, [switch]$ManagementOS, [string]$SwitchName, [switch]$All)
    process {
        Write-MockCall 'Get-VMNetworkAdapter' $PSBoundParameters
        if ($ManagementOS) {
            foreach ($nic in @((Read-MockState).hostAdapters | Where-Object { $null -ne $_ })) {
                if ($SwitchName -and [string]$nic.switchName -ne $SwitchName) { continue }
                [pscustomobject]@{ Name = [string]$nic.switchName; SwitchName = [string]$nic.switchName; MacAddress = [string]$nic.mac; IsManagementOs = $true }
            }
            return
        }
        $row = Get-MockRow $VM
        if ($null -eq $row) { return }
        foreach ($adapter in Get-MockAdapters $row) { if (-not $Name -or [string]$adapter.name -eq $Name) { ConvertTo-MockAdapter $adapter $row.id } }
    }
}

function Add-VMNetworkAdapter {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Name, [string]$SwitchName, [string]$StaticMacAddress, [switch]$DynamicMacAddress, [switch]$Passthru)
    process {
        Write-MockCall 'Add-VMNetworkAdapter' $PSBoundParameters
        Assert-MockFailure 'Add-VMNetworkAdapter'
        if ($VM) {
            $row = Get-MockRow $VM
            $entry = [pscustomobject]@{ name = $(if ($Name) { $Name } else { 'Network Adapter' }); switchName = $(if ($SwitchName) { $SwitchName } else { $null }); mac = '000000000000'; dynamicMac = $true; spoofing = 'Off'; vlan = 0 }
            Update-MockVm $VM 'adapters' (@(Get-MockAdapters $row) + $entry)
            if ($Passthru) { ConvertTo-MockAdapter $entry $row.id }
        }
    }
}

function Remove-VMNetworkAdapter {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [switch]$Passthru)
    process {
        Write-MockCall 'Remove-VMNetworkAdapter' $PSBoundParameters
        if ($VMNetworkAdapter) {
            $state = Read-MockState
            foreach ($row in @($state.vms)) {
                if ([string]$row.id -eq [string]$VMNetworkAdapter.VMId) { Set-MockProperty $row 'adapters' @(Get-MockAdapters $row | Where-Object { [string]$_.name -ne [string]$VMNetworkAdapter.Name }) }
            }
            Write-MockState $state
        }
    }
}

function Set-VMNetworkAdapter {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [string]$StaticMacAddress, [switch]$DynamicMacAddress, [string]$MacAddressSpoofing, [string]$DhcpGuard, [string]$RouterGuard, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMNetworkAdapter' $PSBoundParameters
        if ($VMNetworkAdapter) {
            $values = @{}
            if ($StaticMacAddress) { $values.mac = $StaticMacAddress; $values.dynamicMac = $false }
            if ($DynamicMacAddress) { $values.dynamicMac = $true }
            if ($MacAddressSpoofing) { $values.spoofing = $MacAddressSpoofing }
            Update-MockAdapter $VMNetworkAdapter $values
        }
    }
}

function Set-VMNetworkAdapterVlan {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$VMNetworkAdapterName, [switch]$Untagged, [switch]$Access, [int]$VlanId, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMNetworkAdapterVlan' $PSBoundParameters
        if ($VMNetworkAdapter) { Update-MockAdapter $VMNetworkAdapter @{ vlan = $(if ($Untagged) { 0 } else { $VlanId }) } }
    }
}

function Connect-VMNetworkAdapter {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [string]$SwitchName, [switch]$Passthru)
    process { Write-MockCall 'Connect-VMNetworkAdapter' $PSBoundParameters; if ($VMNetworkAdapter) { Update-MockAdapter $VMNetworkAdapter @{ switchName = $SwitchName } } }
}

function Disconnect-VMNetworkAdapter {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [switch]$Passthru)
    process { Write-MockCall 'Disconnect-VMNetworkAdapter' $PSBoundParameters; if ($VMNetworkAdapter) { Update-MockAdapter $VMNetworkAdapter @{ switchName = $null } } }
}

function Remove-VMSnapshot {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMSnapshot, [string]$Name, [switch]$IncludeAllChildSnapshots, [switch]$Passthru)
    process {
        Write-MockCall 'Remove-VMSnapshot' $PSBoundParameters
        Assert-MockFailure 'Remove-VMSnapshot'
        if (-not $VMSnapshot) { return }
        $state = Read-MockState
        foreach ($row in @($state.vms)) {
            if ([string]$row.id -ne [string]$VMSnapshot.VMId) { continue }
            $remove = @{ ([string]$VMSnapshot.Id) = $true }
            $grew = [bool]$IncludeAllChildSnapshots
            while ($grew) {
                $grew = $false
                foreach ($snapshot in @($row.snapshots | Where-Object { $null -ne $_ })) {
                    if ($snapshot.parentId -and $remove.ContainsKey([string]$snapshot.parentId) -and -not $remove.ContainsKey([string]$snapshot.id)) { $remove[[string]$snapshot.id] = $true; $grew = $true }
                }
            }
            $row.snapshots = @($row.snapshots | Where-Object { $null -ne $_ -and -not $remove.ContainsKey([string]$_.id) })
            if (@($row.snapshots).Count -eq 0) { Set-MockProperty $row 'mergePolls' ([int](Get-MockValue $row 'mergeDelay' 2)) }
        }
        Write-MockState $state
    }
}

function New-VHD {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Path, [long]$SizeBytes, [switch]$Dynamic, [switch]$Fixed, [switch]$Differencing, [string]$ParentPath, [long]$BlockSizeBytes)
    Write-MockCall 'New-VHD' $PSBoundParameters
    Assert-MockFailure 'New-VHD'
    if ([System.IO.File]::Exists($Path)) { Stop-Mock 'The file exists. (0x80070050)' 'CreateVHD,Microsoft.Vhd.PowerShell.Cmdlets.NewVhd' ResourceExists }
    if (-not [System.IO.Directory]::Exists([System.IO.Path]::GetDirectoryName($Path))) { Stop-Mock 'The system cannot find the path specified.' 'ObjectNotFound,Microsoft.Vhd.PowerShell.Cmdlets.NewVhd' ObjectNotFound }
    $state = Read-MockState
    $type = 'Dynamic'; $size = $SizeBytes
    if ($ParentPath) {
        if (-not [System.IO.File]::Exists($ParentPath)) { Stop-Mock "Parent disk not found: $ParentPath" 'ObjectNotFound,Microsoft.Vhd.PowerShell.Cmdlets.NewVhd' ObjectNotFound }
        $type = 'Differencing'; $size = [long](Get-MockValue (Get-MockVhdRow $state $ParentPath) 'size' 68719476736)
    } elseif ($Fixed) { $type = 'Fixed' }
    [System.IO.File]::WriteAllBytes($Path, [byte[]]@())
    Add-MockVhdRow $state ([pscustomobject]@{ path = $Path; type = $type; parentPath = $(if ($ParentPath) { $ParentPath } else { $null }); size = $size })
    Write-MockState $state
    [pscustomobject]@{ Path = $Path; VhdFormat = 'VHDX'; VhdType = $type; ParentPath = $ParentPath; Size = $size; FileSize = [long]4194304; Attached = $false }
}

function Get-VHD {
    [CmdletBinding()]
    param([Parameter(Position = 0, ValueFromPipelineByPropertyName)][Alias('FullName')][string[]]$Path, [guid[]]$VMId)
    process {
        Write-MockCall 'Get-VHD' $PSBoundParameters
        $state = Read-MockState
        foreach ($p in $Path) {
            $row = Get-MockVhdRow $state $p
            if ([bool](Get-MockValue $row 'missing' $false)) { Stop-Mock "The system cannot find the file '$p'." 'ObjectNotFound,Microsoft.Vhd.PowerShell.Cmdlets.GetVHD' ObjectNotFound }
            $parent = [string](Get-MockValue $row 'parentPath' '')
            [pscustomobject]@{
                Path = $p; VhdFormat = $(if ($p -match '\.a?vhd$') { 'VHD' } else { 'VHDX' })
                VhdType = [string](Get-MockValue $row 'type' $(if ($parent) { 'Differencing' } else { 'Dynamic' }))
                ParentPath = $(if ($parent) { $parent } else { $null })
                Size = [long](Get-MockValue $row 'size' 68719476736); FileSize = [long](Get-MockValue $row 'fileSize' 4194304); MinimumSize = $null
                Attached = [bool](Get-MockValue $row 'mounted' $false); DiskIdentifier = '6F1A2B3C-0000-4000-8000-000000000001'
                LogicalSectorSize = 512; PhysicalSectorSize = 4096; BlockSize = 33554432
            }
        }
    }
}

function Set-VHD {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Path, [string]$ParentPath, [switch]$ResetDiskIdentifier, [switch]$Force, [switch]$Passthru)
    Write-MockCall 'Set-VHD' $PSBoundParameters
}

function Export-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [string]$Path, [switch]$Passthru)
    process {
        Write-MockCall 'Export-VM' $PSBoundParameters
        Assert-MockFailure 'Export-VM'
        if ($VM -and $Path) {
            $row = Get-MockRow $VM
            $root = [System.IO.Path]::Combine($Path, [string]$VM.Name)
            $configuration = [System.IO.Path]::Combine($root, 'Virtual Machines')
            $disks = [System.IO.Path]::Combine($root, 'Virtual Hard Disks')
            [void][System.IO.Directory]::CreateDirectory($configuration)
            [void][System.IO.Directory]::CreateDirectory($disks)
            $file = [System.IO.Path]::Combine($configuration, ([string]$VM.Id).ToUpperInvariant() + '.vmcx')
            [System.IO.File]::WriteAllText($file + '.json', ($row | ConvertTo-Json -Depth 12))
            [System.IO.File]::WriteAllBytes($file, [byte[]]@())
            foreach ($disk in @($row.disks | Where-Object { $null -ne $_ })) { [System.IO.File]::WriteAllBytes([System.IO.Path]::Combine($disks, [System.IO.Path]::GetFileName((Get-MockDiskPath $disk))), [byte[]]@()) }
        }
    }
}

function Import-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Path, [switch]$Copy, [switch]$GenerateNewId, [string]$VirtualMachinePath, [string]$VhdDestinationPath, [string]$SnapshotFilePath, [string]$SmartPagingFilePath, [switch]$Register)
    Write-MockCall 'Import-VM' $PSBoundParameters
    Assert-MockFailure 'Import-VM'
    $state = Read-MockState
    if ($Path -and [System.IO.File]::Exists($Path + '.json')) {
        $row = Get-Content -LiteralPath ($Path + '.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $row.id = [guid]::NewGuid().ToString()
        $disks = @()
        if ($VhdDestinationPath) { [void][System.IO.Directory]::CreateDirectory($VhdDestinationPath) }
        foreach ($disk in @($row.disks | Where-Object { $null -ne $_ })) {
            $target = [System.IO.Path]::Combine($VhdDestinationPath, [System.IO.Path]::GetFileName((Get-MockDiskPath $disk)))
            [System.IO.File]::WriteAllBytes($target, [byte[]]@())
            Add-MockVhdRow $state ([pscustomobject]@{ path = $target; type = 'Dynamic'; parentPath = $null; size = [long]68719476736 })
            $disks += $target
        }
        $row.disks = $disks
        $row.snapshots = @()
        Set-MockProperty $row 'path' $VirtualMachinePath
        if ($VirtualMachinePath) {
            $configuration = [System.IO.Path]::Combine($VirtualMachinePath, 'Virtual Machines')
            [void][System.IO.Directory]::CreateDirectory($configuration)
            [System.IO.File]::WriteAllBytes([System.IO.Path]::Combine($configuration, ([string]$row.id).ToUpperInvariant() + '.vmcx'), [byte[]]@())
        }
    } else {
        $row = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = 'Imported ' + [IO.Path]::GetFileNameWithoutExtension($Path); state = 'Off'; generation = 2; processorCount = 2; memoryStartup = [long]4294967296; dynamicMemory = $false; disks = @(); snapshots = @() }
    }
    $state.vms = @($state.vms) + $row
    Write-MockState $state
    ConvertTo-MockVm $row
}

function New-VMSwitch {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Name, [string]$SwitchType, [string]$NetAdapterName, [bool]$AllowManagementOS)
    Write-MockCall 'New-VMSwitch' $PSBoundParameters
    Assert-MockFailure 'New-VMSwitch'
    $state = Read-MockState
    $row = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = $Name; type = $(if ($SwitchType) { $SwitchType } else { 'External' }) }
    $state.switches = @($state.switches) + $row
    if ($SwitchType -eq 'Internal') {
        if ($null -eq $state.PSObject.Properties['hostAdapters']) { $state | Add-Member -NotePropertyName hostAdapters -NotePropertyValue @() }
        $state.hostAdapters = @(@($state.hostAdapters | Where-Object { $null -ne $_ }) + [pscustomobject]@{ switchName = $Name; mac = '00155D' + ('{0:X6}' -f (Get-Random -Maximum 16777215)) })
    }
    Write-MockState $state
    [pscustomobject]@{ Id = [guid]$row.id; Name = $row.name; SwitchType = $row.type; NetAdapterInterfaceDescription = $null }
}

function Get-NetAdapter {
    [CmdletBinding()] param([Parameter(Position = 0)][string[]]$Name, [switch]$IncludeHidden)
    Write-MockCall 'Get-NetAdapter' $PSBoundParameters
    $index = 40
    foreach ($nic in @((Read-MockState).hostAdapters | Where-Object { $null -ne $_ })) {
        [pscustomobject]@{ Name = 'vEthernet (' + [string]$nic.switchName + ')'; MacAddress = ([string]$nic.mac -replace '(..)(?!$)', '$1-'); ifIndex = $index }
        $index++
    }
}

function New-NetIPAddress {
    [CmdletBinding(SupportsShouldProcess)] param([int]$InterfaceIndex, [string]$IPAddress, [int]$PrefixLength)
    Write-MockCall 'New-NetIPAddress' $PSBoundParameters
    Assert-MockFailure 'New-NetIPAddress'
    [pscustomobject]@{ InterfaceIndex = $InterfaceIndex; IPAddress = $IPAddress; PrefixLength = $PrefixLength }
}

function Get-NetNat {
    [CmdletBinding()] param([Parameter(Position = 0)][string]$Name)
    Write-MockCall 'Get-NetNat' $PSBoundParameters
    foreach ($nat in @((Read-MockState).nats | Where-Object { $null -ne $_ })) {
        if (-not $Name -or [string]$nat.name -eq $Name) { [pscustomobject]@{ Name = [string]$nat.name; InternalIPInterfaceAddressPrefix = [string]$nat.prefix } }
    }
}

function New-NetNat {
    [CmdletBinding(SupportsShouldProcess)] param([string]$Name, [string]$InternalIPInterfaceAddressPrefix)
    Write-MockCall 'New-NetNat' $PSBoundParameters
    Assert-MockFailure 'New-NetNat'
    $state = Read-MockState
    if ($null -eq $state.PSObject.Properties['nats']) { $state | Add-Member -NotePropertyName nats -NotePropertyValue @() }
    $state.nats = @(@($state.nats | Where-Object { $null -ne $_ }) + [pscustomobject]@{ name = $Name; prefix = $InternalIPInterfaceAddressPrefix })
    Write-MockState $state
    [pscustomobject]@{ Name = $Name; InternalIPInterfaceAddressPrefix = $InternalIPInterfaceAddressPrefix }
}

function Remove-NetNat {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(Position = 0)][string]$Name)
    Write-MockCall 'Remove-NetNat' $PSBoundParameters
    $state = Read-MockState
    if ($null -ne $state.PSObject.Properties['nats']) { $state.nats = @($state.nats | Where-Object { $null -ne $_ -and [string]$_.name -ne $Name }) }
    Write-MockState $state
}
