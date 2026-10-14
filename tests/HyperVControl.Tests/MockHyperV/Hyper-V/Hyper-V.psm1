# Recording mock of the Hyper-V PowerShell module for HyperVControl unit tests.
# The test harness selects it through HYPERV_CONTROL_MODULE_PATH; it is never shipped or installed.
# State: the JSON file named by HYPERV_CONTROL_MOCK_STATE ({ vms: [...], switches: [...] }).
# Calls: every cmdlet appends { cmdlet, parameters } to the JSON-lines file HYPERV_CONTROL_MOCK_LOG,
# so tests assert on exactly which cmdlets ran with which bound parameters.
# Feature areas add cmdlets in *.functions.ps1 files beside this module; they are dot-sourced below.
Set-StrictMode -Off
$script:StatePath = $env:HYPERV_CONTROL_MOCK_STATE
$script:LogPath = $env:HYPERV_CONTROL_MOCK_LOG

function Read-MockState {
    if ($script:StatePath -and (Test-Path -LiteralPath $script:StatePath)) {
        $state = Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $state.PSObject.Properties['vms']) { $state | Add-Member -NotePropertyName vms -NotePropertyValue @() }
        if ($null -eq $state.PSObject.Properties['switches']) { $state | Add-Member -NotePropertyName switches -NotePropertyValue @() }
        return $state
    }
    return [pscustomobject]@{ vms = @(); switches = @() }
}

function Write-MockState($State) {
    if ($script:StatePath) { $State | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8 }
}

function ConvertTo-MockLogValue($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [switch]) { return [bool]$Value }
    if ($Value -is [bool] -or $Value -is [string] -or $Value -is [int] -or $Value -is [long] -or $Value -is [double]) { return $Value }
    if ($Value -is [guid]) { return [string]$Value }
    if ($Value -is [array]) { return @($Value | ForEach-Object { ConvertTo-MockLogValue $_ }) }
    if ($Value -is [pscustomobject] -and $null -ne $Value.PSObject.Properties['Id']) { return [string]$Value.Id }
    return [string]$Value
}

function Write-MockCall([string]$Cmdlet, [System.Collections.IDictionary]$Parameters) {
    $entry = [ordered]@{ cmdlet = $Cmdlet; parameters = [ordered]@{} }
    foreach ($key in @($Parameters.Keys)) { $entry.parameters[$key] = ConvertTo-MockLogValue $Parameters[$key] }
    if ($script:LogPath) { Add-Content -LiteralPath $script:LogPath -Value ($entry | ConvertTo-Json -Compress -Depth 6) -Encoding UTF8 }
}

function ConvertTo-MockVm($Row) {
    [pscustomobject]@{
        Id = [guid]$Row.id
        Name = [string]$Row.name
        State = [string]$Row.state
        Generation = [int]$Row.generation
        Version = '12.0'
        ProcessorCount = [int]$Row.processorCount
        MemoryStartup = [long]$Row.memoryStartup
        MemoryAssigned = [long]0
        MemoryMinimum = [long]536870912
        MemoryMaximum = [long]1099511627776
        DynamicMemoryEnabled = [bool]$Row.dynamicMemory
        CheckpointType = 'Production'
        ParentSnapshotId = $null
        Path = 'C:\MockVMs'
        Uptime = [timespan]::Zero
        GuestOperatingSystem = $null
        Notes = ''
    }
}

function Get-MockRow($Vm) {
    $state = Read-MockState
    return @($state.vms | Where-Object { [string]$_.id -eq [string]$Vm.Id }) | Select-Object -First 1
}

function Update-MockVm($Vm, [string]$Property, $Value) {
    $state = Read-MockState
    foreach ($row in @($state.vms)) {
        if ([string]$row.id -eq [string]$Vm.Id) {
            if ($null -eq $row.PSObject.Properties[$Property]) { $row | Add-Member -NotePropertyName $Property -NotePropertyValue $Value }
            else { $row.$Property = $Value }
        }
    }
    Write-MockState $state
}

function Remove-MockVm($Vm) {
    $state = Read-MockState
    $state.vms = @($state.vms | Where-Object { [string]$_.id -ne [string]$Vm.Id })
    Write-MockState $state
}

function Get-VM {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string[]]$Name, [guid[]]$Id)
    Write-MockCall 'Get-VM' $PSBoundParameters
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
    [pscustomobject]@{ VirtualHardDiskPath = 'C:\MockVHDs'; VirtualMachinePath = 'C:\MockVMs'; EnableEnhancedSessionMode = $true; LogicalProcessorCount = 8; MemoryCapacity = [long]34359738368 }
}

function Get-VMHostSupportedVersion {
    [CmdletBinding()] param([switch]$Default)
    Write-MockCall 'Get-VMHostSupportedVersion' $PSBoundParameters
    if ($Default) { [pscustomobject]@{ Name = 'Microsoft Windows 11'; Version = [version]'12.0'; IsDefault = $true } }
    else { foreach ($v in '8.0', '9.0', '10.0', '11.0', '12.0') { [pscustomobject]@{ Name = "Version $v"; Version = [version]$v; IsDefault = ($v -eq '12.0') } } }
}

function Get-VMSwitch {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string[]]$Name, [guid[]]$Id)
    Write-MockCall 'Get-VMSwitch' $PSBoundParameters
    foreach ($row in @((Read-MockState).switches)) {
        if ($PSBoundParameters.ContainsKey('Name') -and @($Name | Where-Object { [string]$row.name -like $_ }).Count -eq 0) { continue }
        if ($PSBoundParameters.ContainsKey('Id') -and @($Id | Where-Object { [string]$_ -eq [string]$row.id }).Count -eq 0) { continue }
        [pscustomobject]@{ Id = [guid]$row.id; Name = [string]$row.name; SwitchType = [string]$row.type; NetAdapterInterfaceDescription = $null }
    }
}

function New-VMSwitch {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Name, [string]$SwitchType, [string]$NetAdapterName, [bool]$AllowManagementOS)
    Write-MockCall 'New-VMSwitch' $PSBoundParameters
    $state = Read-MockState
    $row = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = $Name; type = $(if ($SwitchType) { $SwitchType } else { 'External' }) }
    $state.switches = @($state.switches) + $row
    Write-MockState $state
    [pscustomobject]@{ Id = [guid]$row.id; Name = $row.name; SwitchType = $row.type; NetAdapterInterfaceDescription = $null }
}

function Remove-VMSwitch {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VMSwitch, [string[]]$Name, [switch]$Force)
    process {
        Write-MockCall 'Remove-VMSwitch' $PSBoundParameters
        $state = Read-MockState
        $target = if ($VMSwitch) { [string]$VMSwitch.Id } else { $null }
        $state.switches = @($state.switches | Where-Object { ($null -ne $target -and [string]$_.id -ne $target) -or ($null -eq $target -and ($Name -notcontains [string]$_.name)) })
        Write-MockState $state
    }
}

function New-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Name, [string]$Path, [int]$Generation = 1, [long]$MemoryStartupBytes = 536870912,
        [string]$VHDPath, [string]$NewVHDPath, [long]$NewVHDSizeBytes, [string]$SwitchName, [switch]$NoVHD, [string]$Version, [string]$BootDevice, [switch]$Force)
    Write-MockCall 'New-VM' $PSBoundParameters
    $state = Read-MockState
    $disks = @()
    if ($VHDPath) { $disks += $VHDPath } elseif ($NewVHDPath) { $disks += $NewVHDPath }
    $row = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = $Name; state = 'Off'; generation = $Generation; processorCount = 1; memoryStartup = $MemoryStartupBytes; dynamicMemory = $false; disks = $disks; snapshots = @() }
    $state.vms = @($state.vms) + $row
    Write-MockState $state
    ConvertTo-MockVm $row
}

function Remove-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Force)
    process { Write-MockCall 'Remove-VM' $PSBoundParameters; if ($VM) { Remove-MockVm $VM } }
}

function Rename-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string]$Name, [string]$NewName, [switch]$Passthru)
    process { Write-MockCall 'Rename-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'name' $NewName } }
}

function Set-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [int]$ProcessorCount, [long]$MemoryStartupBytes, [long]$MemoryMinimumBytes, [long]$MemoryMaximumBytes,
        [switch]$DynamicMemory, [switch]$StaticMemory, [string]$CheckpointType, [bool]$AutomaticCheckpointsEnabled, [string]$Notes,
        [string]$AutomaticStartAction, [int]$AutomaticStartDelay, [string]$AutomaticStopAction, [string]$NewVMName, [string]$EnhancedSessionTransportType,
        [string]$SnapshotFileLocation, [string]$SmartPagingFilePath, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VM' $PSBoundParameters
        if ($VM) {
            if ($PSBoundParameters.ContainsKey('ProcessorCount')) { Update-MockVm $VM 'processorCount' $ProcessorCount }
            if ($PSBoundParameters.ContainsKey('MemoryStartupBytes')) { Update-MockVm $VM 'memoryStartup' $MemoryStartupBytes }
            if ($DynamicMemory) { Update-MockVm $VM 'dynamicMemory' $true }
            if ($StaticMemory) { Update-MockVm $VM 'dynamicMemory' $false }
            if ($PSBoundParameters.ContainsKey('NewVMName')) { Update-MockVm $VM 'name' $NewVMName }
        }
    }
}

function Get-VMProcessor {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMProcessor' $PSBoundParameters; [pscustomobject]@{ VMId = $VM.Id; Count = $VM.ProcessorCount; Reserve = 0; Maximum = 100; RelativeWeight = 100; ExposeVirtualizationExtensions = $false } }
}

function Set-VMProcessor {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [int]$Count, [long]$Reserve, [long]$Maximum, [int]$RelativeWeight, [bool]$ExposeVirtualizationExtensions, [switch]$Passthru)
    process { Write-MockCall 'Set-VMProcessor' $PSBoundParameters; if ($VM -and $PSBoundParameters.ContainsKey('Count')) { Update-MockVm $VM 'processorCount' $Count } }
}

function Get-VMMemory {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMMemory' $PSBoundParameters; [pscustomobject]@{ VMId = $VM.Id; Startup = $VM.MemoryStartup; Minimum = $VM.MemoryMinimum; Maximum = $VM.MemoryMaximum; DynamicMemoryEnabled = $VM.DynamicMemoryEnabled; Buffer = 20; Priority = 50 } }
}

function Set-VMMemory {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [long]$StartupBytes, [long]$MinimumBytes, [long]$MaximumBytes, [bool]$DynamicMemoryEnabled, [int]$Buffer, [int]$Priority, [switch]$Passthru)
    process {
        Write-MockCall 'Set-VMMemory' $PSBoundParameters
        if ($VM) {
            if ($PSBoundParameters.ContainsKey('StartupBytes')) { Update-MockVm $VM 'memoryStartup' $StartupBytes }
            if ($PSBoundParameters.ContainsKey('DynamicMemoryEnabled')) { Update-MockVm $VM 'dynamicMemory' $DynamicMemoryEnabled }
        }
    }
}

function Start-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Start-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'state' 'Running' } } }
function Stop-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Force, [switch]$TurnOff, [switch]$Save, [switch]$Passthru)
    process { Write-MockCall 'Stop-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'state' $(if ($Save) { 'Saved' } else { 'Off' }) } } }
function Suspend-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Suspend-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'state' 'Paused' } } }
function Resume-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Resume-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'state' 'Running' } } }
function Restart-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [switch]$Force, [switch]$Passthru)
    process { Write-MockCall 'Restart-VM' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'state' 'Running' } } }

function Export-VM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [string]$Path, [switch]$Passthru)
    process { Write-MockCall 'Export-VM' $PSBoundParameters } }

function Import-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Position = 0)][string]$Path, [switch]$Copy, [switch]$GenerateNewId, [string]$VirtualMachinePath, [string]$VhdDestinationPath, [string]$SnapshotFilePath, [string]$SmartPagingFilePath, [switch]$Register)
    Write-MockCall 'Import-VM' $PSBoundParameters
    $state = Read-MockState
    $row = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = 'Imported ' + [IO.Path]::GetFileNameWithoutExtension($Path); state = 'Off'; generation = 2; processorCount = 2; memoryStartup = [long]4294967296; dynamicMemory = $false; disks = @(); snapshots = @() }
    $state.vms = @($state.vms) + $row
    Write-MockState $state
    ConvertTo-MockVm $row
}

function ConvertTo-MockSnapshot($Row, $VmId) {
    [pscustomobject]@{ Id = [guid]$Row.id; VMId = [guid]$VmId; Name = [string]$Row.name; CreationTime = [datetime]$Row.creationTime; ParentSnapshotId = $(if ($Row.parentId) { [guid]$Row.parentId } else { $null }); SnapshotType = 'Standard' }
}

function Get-VMSnapshot {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name, [guid[]]$Id, $ParentOf, [string]$SnapshotType)
    process {
        Write-MockCall 'Get-VMSnapshot' $PSBoundParameters
        if ($ParentOf) { return }
        $row = Get-MockRow $VM
        if ($null -eq $row) { return }
        foreach ($snapshot in @($row.snapshots)) { if ($null -ne $snapshot) { ConvertTo-MockSnapshot $snapshot $VM.Id } }
    }
}

function Checkpoint-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$VM, [string[]]$Name, [string]$SnapshotName, [switch]$Passthru)
    process {
        Write-MockCall 'Checkpoint-VM' $PSBoundParameters
        $snapshot = [pscustomobject]@{ id = [guid]::NewGuid().ToString(); name = $SnapshotName; creationTime = [datetime]::UtcNow.ToString('o'); parentId = $null }
        $row = Get-MockRow $VM
        $snapshots = @()
        if ($row -and $row.snapshots) { $snapshots = @($row.snapshots) }
        Update-MockVm $VM 'snapshots' (@($snapshots) + $snapshot)
        if ($Passthru) { ConvertTo-MockSnapshot $snapshot $VM.Id }
    }
}

function Restore-VMSnapshot { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMSnapshot, [string]$Name, [switch]$Passthru)
    process { Write-MockCall 'Restore-VMSnapshot' $PSBoundParameters } }
function Remove-VMSnapshot { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMSnapshot, [string]$Name, [switch]$IncludeAllChildSnapshots, [switch]$Passthru)
    process { Write-MockCall 'Remove-VMSnapshot' $PSBoundParameters } }
function Rename-VMSnapshot { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMSnapshot, [string]$Name, [string]$NewName, [switch]$Passthru)
    process { Write-MockCall 'Rename-VMSnapshot' $PSBoundParameters } }

function Get-VMFirmware { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMFirmware' $PSBoundParameters; [pscustomobject]@{ VMId = $VM.Id; SecureBoot = 'On'; SecureBootTemplate = 'MicrosoftWindows'; PreferredNetworkBootProtocol = 'IPv4'; BootOrder = @() } } }
function Set-VMFirmware { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$EnableSecureBoot, [string]$SecureBootTemplate, $FirstBootDevice, $BootOrder, [string]$PreferredNetworkBootProtocol, [switch]$Passthru)
    process { Write-MockCall 'Set-VMFirmware' $PSBoundParameters } }

function Get-VMNetworkAdapter { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Name, [switch]$All)
    process { Write-MockCall 'Get-VMNetworkAdapter' $PSBoundParameters; if ($VM) { [pscustomobject]@{ Id = 'Microsoft:' + [string]$VM.Id + '\7B2C38CE-D044-4725-9470-15D22D5D7B8E'; VMId = $VM.Id; Name = 'Network Adapter'; SwitchName = 'Default Switch'; MacAddress = '00155D000001'; IPAddresses = @(); Status = $null } } } }
function Add-VMNetworkAdapter { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Name, [string]$SwitchName, [switch]$Passthru)
    process { Write-MockCall 'Add-VMNetworkAdapter' $PSBoundParameters } }
function Remove-VMNetworkAdapter { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [switch]$Passthru)
    process { Write-MockCall 'Remove-VMNetworkAdapter' $PSBoundParameters } }
function Set-VMNetworkAdapter { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [string]$StaticMacAddress, [switch]$DynamicMacAddress, [string]$MacAddressSpoofing, [string]$DhcpGuard, [string]$RouterGuard, [switch]$Passthru)
    process { Write-MockCall 'Set-VMNetworkAdapter' $PSBoundParameters } }
function Connect-VMNetworkAdapter { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [string]$SwitchName, [switch]$Passthru)
    process { Write-MockCall 'Connect-VMNetworkAdapter' $PSBoundParameters } }
function Disconnect-VMNetworkAdapter { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMNetworkAdapter, $VM, [string[]]$VMName, [string]$Name, [switch]$Passthru)
    process { Write-MockCall 'Disconnect-VMNetworkAdapter' $PSBoundParameters } }

function Get-VMHardDiskDrive { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation)
    process {
        Write-MockCall 'Get-VMHardDiskDrive' $PSBoundParameters
        $row = Get-MockRow $VM
        if ($null -eq $row) { return }
        $location = 0
        foreach ($disk in @($row.disks)) { if ($null -ne $disk) { [pscustomobject]@{ VMId = $VM.Id; Path = [string]$disk; ControllerType = 'SCSI'; ControllerNumber = 0; ControllerLocation = $location }; $location++ } }
    } }
function Add-VMHardDiskDrive { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Path, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation, [switch]$Passthru)
    process { Write-MockCall 'Add-VMHardDiskDrive' $PSBoundParameters; if ($VM) { $row = Get-MockRow $VM; $disks = @(); if ($row -and $row.disks) { $disks = @($row.disks) }; Update-MockVm $VM 'disks' (@($disks) + $Path) } } }
function Remove-VMHardDiskDrive { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMHardDiskDrive, [string[]]$VMName, [string]$ControllerType, [int]$ControllerNumber, [int]$ControllerLocation, [switch]$Passthru)
    process { Write-MockCall 'Remove-VMHardDiskDrive' $PSBoundParameters } }

function Get-VMDvdDrive { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [int]$ControllerNumber, [int]$ControllerLocation)
    process { Write-MockCall 'Get-VMDvdDrive' $PSBoundParameters; $row = Get-MockRow $VM; if ($row -and $row.dvd) { [pscustomobject]@{ VMId = $VM.Id; Path = [string]$row.dvd; ControllerNumber = 0; ControllerLocation = 1 } } } }
function Add-VMDvdDrive { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string]$Path, [int]$ControllerNumber, [int]$ControllerLocation, [switch]$Passthru)
    process { Write-MockCall 'Add-VMDvdDrive' $PSBoundParameters; if ($VM) { Update-MockVm $VM 'dvd' $Path; if ($Passthru) { [pscustomobject]@{ VMId = $VM.Id; Path = $Path; ControllerNumber = 0; ControllerLocation = 1 } } } } }
function Set-VMDvdDrive { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMDvdDrive, [string[]]$VMName, [string]$Path, [switch]$Passthru)
    process { Write-MockCall 'Set-VMDvdDrive' $PSBoundParameters } }
function Remove-VMDvdDrive { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VMDvdDrive, [string[]]$VMName, [switch]$Passthru)
    process { Write-MockCall 'Remove-VMDvdDrive' $PSBoundParameters } }

function New-VHD { [CmdletBinding(SupportsShouldProcess)] param([Parameter(Position = 0)][string]$Path, [long]$SizeBytes, [switch]$Dynamic, [switch]$Fixed, [switch]$Differencing, [string]$ParentPath, [long]$BlockSizeBytes)
    Write-MockCall 'New-VHD' $PSBoundParameters
    [pscustomobject]@{ Path = $Path; VhdFormat = 'VHDX'; VhdType = $(if ($Differencing) { 'Differencing' } elseif ($Fixed) { 'Fixed' } else { 'Dynamic' }); ParentPath = $ParentPath; Size = $SizeBytes; FileSize = [long]4194304; Attached = $false } }
function Get-VHD { [CmdletBinding()] param([Parameter(Position = 0, ValueFromPipeline)][string[]]$Path, [guid[]]$VMId)
    process { Write-MockCall 'Get-VHD' $PSBoundParameters; foreach ($p in $Path) { [pscustomobject]@{ Path = $p; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; ParentPath = $null; Size = [long]68719476736; FileSize = [long]4194304; Attached = $false; DiskIdentifier = [guid]::NewGuid().ToString() } } } }
function Resize-VHD { [CmdletBinding(SupportsShouldProcess)] param([Parameter(Position = 0)][string]$Path, [long]$SizeBytes, [switch]$ToMinimumSize, [switch]$Passthru)
    Write-MockCall 'Resize-VHD' $PSBoundParameters }
function Test-VHD { [CmdletBinding()] param([Parameter(Position = 0)][string]$Path) Write-MockCall 'Test-VHD' $PSBoundParameters; $true }

function Get-VMComPort { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [int]$Number)
    process { Write-MockCall 'Get-VMComPort' $PSBoundParameters; foreach ($n in 1, 2) { if (-not $PSBoundParameters.ContainsKey('Number') -or $Number -eq $n) { [pscustomobject]@{ VMId = $VM.Id; Name = "COM $n"; Path = ''; Number = $n } } } } }
function Set-VMComPort { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [int]$Number, [string]$Path, [switch]$Passthru)
    process { Write-MockCall 'Set-VMComPort' $PSBoundParameters } }

function Get-VMIntegrationService { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name)
    process { Write-MockCall 'Get-VMIntegrationService' $PSBoundParameters; foreach ($service in 'Guest Service Interface', 'Heartbeat', 'Key-Value Pair Exchange', 'Shutdown', 'Time Synchronization', 'VSS') { [pscustomobject]@{ VMId = $VM.Id; Id = 'Microsoft:' + [string]$VM.Id + '\' + [guid]::NewGuid().ToString().ToUpperInvariant(); Name = $service; Enabled = ($service -ne 'Guest Service Interface'); PrimaryStatusDescription = $null } } } }
function Enable-VMIntegrationService { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Enable-VMIntegrationService' $PSBoundParameters } }
function Disable-VMIntegrationService { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [string[]]$Name, [switch]$Passthru)
    process { Write-MockCall 'Disable-VMIntegrationService' $PSBoundParameters } }

function Set-VMKeyProtector { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$NewLocalKeyProtector, [switch]$RestoreLastKnownGoodKeyProtector, [byte[]]$KeyProtector, [switch]$Passthru)
    process { Write-MockCall 'Set-VMKeyProtector' $PSBoundParameters } }
function Get-VMKeyProtector { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMKeyProtector' $PSBoundParameters; [byte[]]@() } }
function Enable-VMTPM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$Passthru)
    process { Write-MockCall 'Enable-VMTPM' $PSBoundParameters } }
function Disable-VMTPM { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName, [switch]$Passthru)
    process { Write-MockCall 'Disable-VMTPM' $PSBoundParameters } }
function Get-VMSecurity { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$VM, [string[]]$VMName)
    process { Write-MockCall 'Get-VMSecurity' $PSBoundParameters; [pscustomobject]@{ VMId = $VM.Id; TpmEnabled = $false; KsdEnabled = $false; Shielded = $false; EncryptStateAndVmMigrationTraffic = $false; VirtualizationBasedSecurityOptOut = $false } } }

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.functions.ps1' | ForEach-Object { . $_.FullName }
Export-ModuleMember -Function *
