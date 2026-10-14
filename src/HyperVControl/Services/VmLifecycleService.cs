using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>A <c>hyperv_vm_create</c> request; the defaults are the tool's.</summary>
internal sealed record VmCreateRequest(string Name)
{
    public string Source { get; init; } = "new_disk";
    public string? VhdPath { get; init; }
    public string? ParentPath { get; init; }
    public string? IsoPath { get; init; }
    public string? Path { get; init; }
    public int Generation { get; init; } = 2;
    public int MemoryMb { get; init; } = 4096;
    public bool DynamicMemory { get; init; }
    public int? MemoryMinMb { get; init; }
    public int? MemoryMaxMb { get; init; }
    public int Processors { get; init; } = 2;
    public int DiskGb { get; init; } = 64;
    public string? SwitchName { get; init; }
    public string OsFamily { get; init; } = "windows";
    public bool? SecureBoot { get; init; }
    public bool? Tpm { get; init; }
    public string? Notes { get; init; }
    public bool AutoCheckpoints { get; init; }
    public string CheckpointType { get; init; } = "Standard";
    public bool Start { get; init; }
    public bool AllowDuplicateName { get; init; }
}

/// <summary>A <c>hyperv_vm_configure</c> request; null leaves a setting unchanged.</summary>
internal sealed record VmConfigureRequest
{
    public int? Processors { get; init; }
    public int? MemoryMb { get; init; }
    public bool? DynamicMemory { get; init; }
    public string? CheckpointType { get; init; }
    public string? NewName { get; init; }
    public int? MemoryMinMb { get; init; }
    public int? MemoryMaxMb { get; init; }
    public int? MemoryBufferPercent { get; init; }
    public string? Notes { get; init; }
    public string? AutoStartAction { get; init; }
    public int? AutoStartDelaySeconds { get; init; }
    public string? AutoStopAction { get; init; }
    public bool? AutoCheckpoints { get; init; }
    public bool? NestedVirtualization { get; init; }
    public IReadOnlyDictionary<string, bool>? IntegrationServices { get; init; }
    public string? EnhancedSessionTransport { get; init; }
}

/// <summary>
/// VM lifecycle and hardware operations on the local host. Every operation builds one
/// <see cref="HyperVScript"/> and runs it through the shared runner, so a tool call costs one
/// PowerShell process, every failure carries the uniform envelope, and tests can drive this
/// layer against a mock Hyper-V module without a Hyper-V host. Changes pass the
/// <see cref="VmStateRules"/> gate before the first mutation.
/// </summary>
internal sealed partial class VmLifecycleService(PowerShellRunner runner)
{
    private const string Success = "@{ success = $true }";
    private const string Refresh = "$vm = Hyper-V\\Get-VM -Id $vm.Id -ErrorAction Stop";
    private const string Projection = "(ConvertTo-ControlVm $vm)";
    private static readonly string[] Sources = ["new_disk", "existing_vhd", "iso", "linked", "none"];
    private static readonly string[] OsFamilies = ["windows", "linux", "other"];
    private static readonly string[] CheckpointTypes = ["Standard", "Production", "ProductionOnly", "Disabled"];
    private static readonly string[] StartActions = ["Nothing", "StartIfRunning", "Start"];
    private static readonly string[] StopActions = ["TurnOff", "Save", "ShutDown"];
    private static readonly string[] SessionTransports = ["VMBus", "HvSocket"];

    /// <summary>Locale-independent integration service IDs (the suffix of each service's Id), by English name.</summary>
    internal static readonly IReadOnlyDictionary<string, string> IntegrationServiceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Guest Service Interface"] = "6C09BB55-D683-4DA0-8931-C9BF705F6480",
        ["Heartbeat"] = "84EAAE65-2F2E-45F5-9BB5-0E857DC8EB47",
        ["Key-Value Pair Exchange"] = "2A34B1C2-FD73-4043-8A5B-DD2159BC743F",
        ["Shutdown"] = "9F8233AC-BE49-4C79-8EE3-E7E1985B2077",
        ["Time Synchronization"] = "2497F4DE-E9FA-4204-80E4-4B75C46419C0",
        ["VSS"] = "5CED1297-4598-4915-A5FC-AD21BB4D02A4",
    };

    private static PsValue VmRef => PsValue.Raw("$vm");
    private static string L(string value) => PowerShellRunner.Literal(value);
    private static string B(bool value) => value ? "$true" : "$false";

    /// <summary>A script with the shared lifecycle functions loaded.</summary>
    private static HyperVScript Script() => new HyperVScript().Line(VmScriptLibrary.Functions);

    /// <summary>One command rendered by <see cref="HyperVScript.Cmd"/>, for use inside a larger line.</summary>
    private static string Render(string command, params (string Name, PsValue? Value)[] parameters) =>
        new HyperVScript().Cmd(command, parameters).Body.TrimEnd('\n');

    /// <summary>A PowerShell array of string literals.</summary>
    private static string Array(IEnumerable<string> values) => "@(" + string.Join(", ", values.Select(L)) + ")";

    private static string Choice(string? value, string[] allowed, string parameter) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value
            : throw new ArgumentException($"{parameter} must be one of: {string.Join(", ", allowed)}.");

    private static void Memory(int megabytes, string parameter)
    {
        if (megabytes is < 32 or > 1048576 || megabytes % 2 != 0)
            throw new ArgumentException($"{parameter} must be an even number of MB from 32 to 1048576.");
    }

    private static string Extension(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();

    private static void VmName(string? name, string parameter)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name != name.Trim() || name.EndsWith('.')
            || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException($"{parameter} must be 1-100 characters without \\ / : * ? \" < > |, control characters, or leading/trailing spaces (the name becomes a folder).");
    }

    public Task<JsonElement> InfoAsync(string machine, CancellationToken ct) => runner.InvokeAsync(Script().Vm(machine).Line("""
        $__memory = $null; $__processor = $null; $__bios = $null
        try { $__memory = Get-VMMemory -VM $vm } catch { }
        try { $__processor = Get-VMProcessor -VM $vm } catch { }
        if ([int]$vm.Generation -ne 2) {
            try { $__biosInfo = Get-VMBios -VM $vm; $__bios = [ordered]@{ StartupOrder = @($__biosInfo.StartupOrder | ForEach-Object { [string]$_ }); NumLockEnabled = [bool]$__biosInfo.NumLockEnabled } } catch { }
        }
        $__info = [ordered]@{
            vm = ($vm | Select-Object Id,Name,State,Generation,Version,ProcessorCount,MemoryStartup,MemoryAssigned,MemoryMinimum,MemoryMaximum,DynamicMemoryEnabled,CheckpointType,ParentSnapshotId,Path)
            state = [string]$vm.State
            configurationVersion = Get-ControlText $vm.Version
            creationTime = $(if ($null -ne $vm.CreationTime) { ([datetime]$vm.CreationTime).ToUniversalTime().ToString('o') } else { $null })
            notes = [string]$vm.Notes
            memory = [ordered]@{
                startupBytes = [long]$vm.MemoryStartup; assignedBytes = [long]$vm.MemoryAssigned
                dynamicMemoryEnabled = [bool]$vm.DynamicMemoryEnabled; minimumBytes = [long]$vm.MemoryMinimum; maximumBytes = [long]$vm.MemoryMaximum
                bufferPercent = $(if ($null -ne $__memory) { [int]$__memory.Buffer } else { $null })
            }
            processor = [ordered]@{
                count = [int]$vm.ProcessorCount
                nestedVirtualization = $(if ($null -ne $__processor) { [bool]$__processor.ExposeVirtualizationExtensions } else { $null })
                reservePercent = $(if ($null -ne $__processor) { [long]$__processor.Reserve } else { $null })
                limitPercent = $(if ($null -ne $__processor) { [long]$__processor.Maximum } else { $null })
                relativeWeight = $(if ($null -ne $__processor) { [int]$__processor.RelativeWeight } else { $null })
            }
            automaticActions = [ordered]@{
                startAction = Get-ControlText $vm.AutomaticStartAction; startDelaySeconds = [int]$vm.AutomaticStartDelay
                stopAction = Get-ControlText $vm.AutomaticStopAction; checkpointsEnabled = [bool]$vm.AutomaticCheckpointsEnabled
            }
            checkpointType = Get-ControlText $vm.CheckpointType
            checkpointCount = @(Get-VMSnapshot -VM $vm).Count
            enhancedSessionTransport = Get-ControlText $vm.EnhancedSessionTransportType
            firmware = (ConvertTo-ControlFirmware $vm)
            bios = $__bios
            security = (ConvertTo-ControlSecurity $vm)
            network = @(Get-VMNetworkAdapter -VM $vm | ForEach-Object { ConvertTo-ControlAdapter $_ })
            disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { ConvertTo-ControlDrive $_ })
            dvd = @(Get-VMDvdDrive -VM $vm | Select-Object Path,ControllerNumber,ControllerLocation)
            com = @(Get-VMComPort -VM $vm | Select-Object Name,Path)
            integration = @(Get-VMIntegrationService -VM $vm | Select-Object Id,Name,Enabled,PrimaryStatusDescription)
        }
        """).Returns("$__info"), ct);

    /// <summary>Host defaults for <c>hyperv_capabilities</c>: default paths, configuration versions,
    /// Secure Boot templates, the Default Switch and free space on the default volumes.</summary>
    public Task<JsonElement> HostDefaultsAsync(CancellationToken ct) => runner.InvokeAsync(Script().Line($$"""
        $__host = Get-VMHost
        $__versions = @(Get-VMHostSupportedVersion | ForEach-Object { [string]$_.Version })
        $__default = Get-VMHostSupportedVersion -Default
        $__switch = @(Get-VMSwitch | Where-Object { [string]$_.Id -eq '{{VmScriptLibrary.DefaultSwitchId}}' }) | Select-Object -First 1
        $__defaults = [ordered]@{
            virtualMachinePath = [string]$__host.VirtualMachinePath
            virtualHardDiskPath = [string]$__host.VirtualHardDiskPath
            configurationVersions = [ordered]@{ supported = $__versions; default = $(if ($null -ne $__default) { [string]$__default.Version } else { $null }) }
            secureBootTemplates = @($__host.SecureBootTemplates | Where-Object { $null -ne $_ } | ForEach-Object { if ($null -ne $_.PSObject.Properties['Name']) { [string]$_.Name } else { [string]$_ } })
            defaultSwitch = [ordered]@{ present = ($null -ne $__switch); id = '{{VmScriptLibrary.DefaultSwitchId}}'; name = $(if ($null -ne $__switch) { [string]$__switch.Name } else { $null }) }
            freeSpace = @(@([string]$__host.VirtualMachinePath, [string]$__host.VirtualHardDiskPath) | ForEach-Object { Get-ControlFreeSpace $_ } | Where-Object { $null -ne $_ })
        }
        """).Returns("$__defaults"), ct);

    public Task<JsonElement> CreateAsync(VmCreateRequest r, CancellationToken ct)
    {
        VmName(r.Name, "name");
        var source = Choice(r.Source, Sources, "source");
        var os = Choice(r.OsFamily, OsFamilies, "osFamily");
        Choice(r.CheckpointType, CheckpointTypes, "checkpointType");
        if (r.Generation is not (1 or 2)) throw new ArgumentException("generation must be 1 or 2.");
        var gen2 = r.Generation == 2;
        Memory(r.MemoryMb, "memoryMb");
        if (r.Processors is < 1 or > 2048 || (!gen2 && r.Processors > 64)) throw new ArgumentException("processors must be 1-2048 (1-64 on Generation 1).");
        if (r.Path is not null) ControlService.FullPath(r.Path);
        if (r.VhdPath is not null) ControlService.FullPath(r.VhdPath);
        if (r.ParentPath is not null) ControlService.FullPath(r.ParentPath);
        if (r.IsoPath is not null) ControlService.FullPath(r.IsoPath);
        if (r.IsoPath is not null && source != "iso") throw new ArgumentException("isoPath applies only to source=iso.");
        if (r.ParentPath is not null && source != "linked") throw new ArgumentException("parentPath applies only to source=linked.");
        if (r.VhdPath is not null && source == "none") throw new ArgumentException("source=none creates no disk; omit vhdPath.");
        if (source is "new_disk" or "iso" && r.DiskGb is < 1 or > 65536) throw new ArgumentException("diskGb must be 1-65536.");

        int? minMb = null, maxMb = null;
        if (r.DynamicMemory)
        {
            minMb = r.MemoryMinMb ?? Math.Min(512, r.MemoryMb);
            maxMb = r.MemoryMaxMb ?? 1048576;
            Memory(minMb.Value, "memoryMinMb"); Memory(maxMb.Value, "memoryMaxMb");
            if (minMb > r.MemoryMb || r.MemoryMb > maxMb) throw new ArgumentException("Dynamic memory needs memoryMinMb <= memoryMb <= memoryMaxMb.");
        }
        else if (r.MemoryMinMb is not null || r.MemoryMaxMb is not null) throw new ArgumentException("memoryMinMb and memoryMaxMb need dynamicMemory=true.");

        switch (source)
        {
            case "new_disk" or "iso" when r.VhdPath is not null && Extension(r.VhdPath) != ".vhdx":
                throw new ArgumentException("A new disk must be a .vhdx file.");
            case "iso" when r.IsoPath is null || Extension(r.IsoPath) != ".iso":
                throw new ArgumentException("source=iso needs isoPath to an .iso file.");
            case "existing_vhd" when r.VhdPath is null || Extension(r.VhdPath) is not (".vhdx" or ".vhd"):
                throw new ArgumentException("source=existing_vhd needs vhdPath to a .vhdx (or, for Generation 1, .vhd) file.");
            case "linked" when r.ParentPath is null || Extension(r.ParentPath) is not (".vhdx" or ".vhd"):
                throw new ArgumentException("source=linked needs parentPath to the read-only parent .vhdx.");
            case "linked" when r.VhdPath is not null && Extension(r.VhdPath) != Extension(r.ParentPath!):
                throw new ArgumentException("A differencing disk must use its parent's format (.vhdx or .vhd).");
        }
        var disk = source switch { "existing_vhd" => r.VhdPath, "linked" => r.ParentPath, _ => null };
        if (gen2 && disk is not null && Extension(disk) == ".vhd") throw new ArgumentException("Generation 2 VMs need .vhdx disks.");
        if (!gen2 && r.SecureBoot == true) throw new ArgumentException("Secure Boot needs a Generation 2 VM.");
        if (!gen2 && r.Tpm == true) throw new ArgumentException("A virtual TPM needs a Generation 2 VM.");
        var secureBoot = gen2 && (r.SecureBoot ?? os != "other");
        var template = os == "windows" ? "MicrosoftWindows" : "MicrosoftUEFICertificateAuthority";
        var tpm = gen2 && (r.Tpm ?? os == "windows");

        var s = Script()
            .Line("$__warnings = New-Object System.Collections.ArrayList")
            .Line("$__created = New-Object System.Collections.ArrayList")
            .Line("$__name = " + L(r.Name))
            .Line("$__host = Get-VMHost")
            .Line("$__vmRoot = " + (r.Path is null ? "[string]$__host.VirtualMachinePath" : L(r.Path)))
            .Line("$__vhdRoot = " + (r.Path is null ? "[string]$__host.VirtualHardDiskPath" : "[System.IO.Path]::Combine($__vmRoot, $__name, 'Virtual Hard Disks')"))
            .Line("$__same = @(Hyper-V\\Get-VM | Where-Object { $_.Name -eq $__name })");
        s.Line(r.AllowDuplicateName
            ? "if ($__same.Count -gt 0) { [void]$__warnings.Add('Another VM already has this name; use this VM''s ID in later calls.') }"
            : "if ($__same.Count -gt 0) { Stop-Control 'InvalidArgument' (\"A VM named '\" + $__name + \"' already exists (ID \" + $__same[0].Id + \"). Choose another name, or pass allowDuplicateName=true; later calls must then use the VM ID.\") }");

        s.Line("$__switches = @(Get-VMSwitch)");
        if (r.SwitchName is null)
            s.Line($$"""
                $__switch = @($__switches | Where-Object { [string]$_.Id -eq '{{VmScriptLibrary.DefaultSwitchId}}' }) | Select-Object -First 1
                if ($null -eq $__switch) {
                    $__switch = $__switches | Select-Object -First 1
                    if ($null -eq $__switch) { [void]$__warnings.Add('No virtual switch exists; the network adapter is not connected.') }
                    else { [void]$__warnings.Add("The Default Switch is missing; the network adapter is connected to '" + $__switch.Name + "'.") }
                }
                """);
        else if (r.SwitchName.Length == 0) s.Line("$__switch = $null");
        else s.Line("$__switch = @($__switches | Where-Object { $_.Name -eq " + L(r.SwitchName) + " })")
            .Line("if ($__switch.Count -eq 0) { Stop-Control 'NotFound' ('Virtual switch ' + " + L(L(r.SwitchName)) + " + ' not found. Call hyperv_network action=list.') }")
            .Line("if ($__switch.Count -gt 1) { Stop-Control 'AmbiguousName' 'Several virtual switches have that name.' }")
            .Line("$__switch = $__switch[0]");

        switch (source)
        {
            case "new_disk" or "iso":
                s.Line("$__disk = " + (r.VhdPath is null ? "[System.IO.Path]::Combine($__vhdRoot, $__name + '.vhdx')" : L(r.VhdPath)))
                    .Line("if ([System.IO.File]::Exists($__disk)) { Stop-Control 'InvalidArgument' ('Disk file already exists: ' + $__disk + '. Pass another vhdPath, or source=existing_vhd to use it.') }");
                if (source == "iso")
                    s.Line("$__iso = " + L(r.IsoPath!))
                        .Line("if (-not [System.IO.File]::Exists($__iso)) { Stop-Control 'NotFound' ('ISO not found: ' + $__iso) }");
                s.Line($"Assert-ControlFreeSpace $__disk ([long]{r.DiskGb}*1GB) 'A {r.DiskGb} GB disk'");
                break;
            case "existing_vhd":
                s.Line("$__disk = " + L(r.VhdPath!))
                    .Line("if (-not [System.IO.File]::Exists($__disk)) { Stop-Control 'NotFound' ('Virtual disk not found: ' + $__disk) }")
                    .Line("$__reason = Get-ControlReferenceReason (Get-ControlDiskReferences $null) (Get-ControlPathKey $__disk)")
                    .Line("if ($null -ne $__reason) { Stop-Control 'InvalidArgument' ('Cannot use ' + $__disk + ': it is ' + $__reason + '. Writing to it would corrupt that VM or its children.') }");
                break;
            case "linked":
                s.Line("$__parent = " + L(r.ParentPath!))
                    .Line("if (-not [System.IO.File]::Exists($__parent)) { Stop-Control 'NotFound' ('Parent disk not found: ' + $__parent) }")
                    .Line("$__disk = " + (r.VhdPath is null ? "[System.IO.Path]::Combine($__vhdRoot, $__name + [System.IO.Path]::GetExtension($__parent))" : L(r.VhdPath)))
                    .Line("if ([System.IO.File]::Exists($__disk)) { Stop-Control 'InvalidArgument' ('Disk file already exists: ' + $__disk + '. Pass another vhdPath.') }")
                    .Line("$__users = (Get-ControlDiskReferences $null).Map[(Get-ControlPathKey $__parent)]")
                    .Line("$__attached = @($__users | Where-Object { $null -ne $_ -and $_.Attached })")
                    .Line("if ($__attached.Count -gt 0) { Stop-Control 'InvalidArgument' ('Parent ' + $__parent + ' is ' + (Format-ControlReference $__attached[0]) + '; a linked parent must never change. Use a disk no VM attaches, for example a template from hyperv_vm_clone.') }")
                    .Line("Assert-ControlFreeSpace $__disk ([long](Get-VHD -Path $__parent).Size) ('A differencing disk over ' + $__parent)");
                break;
        }

        (string, PsValue?)[] newVm =
        [
            ("Name", PsValue.Raw("$__name")), ("Path", PsValue.Raw("$__vmRoot")), ("Generation", PsValue.Of(r.Generation)),
            ("MemoryStartupBytes", PsValue.Megabytes(r.MemoryMb)),
            ("NewVHDPath", source is "new_disk" or "iso" ? PsValue.Raw("$__disk") : null),
            ("NewVHDSizeBytes", source is "new_disk" or "iso" ? PsValue.Gigabytes(r.DiskGb) : null),
            ("VHDPath", source is "existing_vhd" or "linked" ? PsValue.Raw("$__disk") : null),
            ("NoVHD", source == "none" ? PsValue.Of(true) : null),
        ];
        s.Line("$vm = $null").Line("try {")
            .Line("if (-not [System.IO.Directory]::Exists($__vmRoot)) { [void][System.IO.Directory]::CreateDirectory($__vmRoot) }");
        if (source != "none" && source != "existing_vhd")
            s.Line("[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($__disk))");
        if (source == "linked")
            s.Cmd("$null = New-VHD", ("Path", PsValue.Raw("$__disk")), ("ParentPath", PsValue.Raw("$__parent")), ("Differencing", PsValue.Of(true))).Line("[void]$__created.Add($__disk)");
        s.Line("if ($null -ne $__switch) { " + Render("$vm = New-VM", [.. newVm, ("SwitchName", PsValue.Raw("$__switch.Name"))]) + " } else { " + Render("$vm = New-VM", newVm) + " }");
        if (source is "new_disk" or "iso") s.Line("[void]$__created.Add($__disk)");
        s.Cmd("Set-VMProcessor", ("VM", VmRef), ("Count", PsValue.Of(r.Processors)));
        // New-VM enables dynamic memory on current Windows hosts, so the memory mode is always set explicitly.
        s.Cmd("Set-VMMemory", ("VM", VmRef), ("DynamicMemoryEnabled", PsValue.Of(r.DynamicMemory)),
            ("MinimumBytes", r.DynamicMemory ? PsValue.Megabytes(minMb!.Value) : null), ("MaximumBytes", r.DynamicMemory ? PsValue.Megabytes(maxMb!.Value) : null));
        s.Cmd("Set-VM", ("VM", VmRef), ("CheckpointType", PsValue.Token(r.CheckpointType, CheckpointTypes)),
            ("AutomaticCheckpointsEnabled", PsValue.Of(r.AutoCheckpoints)), ("Notes", r.Notes is null ? null : PsValue.Of(r.Notes)),
            ("EnhancedSessionTransportType", os == "linux" ? PsValue.Token("HvSocket", SessionTransports) : null));
        if (gen2)
        {
            // The template locks once the vTPM is initialized, so it is set first.
            s.Cmd("Set-VMFirmware", ("VM", VmRef), ("SecureBootTemplate", PsValue.Of(template)))
                .Cmd("Set-VMFirmware", ("VM", VmRef), ("EnableSecureBoot", PsValue.Token(secureBoot ? "On" : "Off", "On", "Off")));
            if (tpm) s.Cmd("Set-VMKeyProtector", ("VM", VmRef), ("NewLocalKeyProtector", PsValue.Of(true))).Cmd("Enable-VMTPM", ("VM", VmRef));
            // New-VM puts the network adapter ahead of the disk (observed live), so every boot would try PXE first.
            if (source != "none")
                s.Line("$__bootDisk = @(Get-VMHardDiskDrive -VM $vm) | Select-Object -First 1")
                    .Cmd("Set-VMFirmware", ("VM", VmRef), ("FirstBootDevice", PsValue.Raw("$__bootDisk")));
        }
        if (source == "iso" && gen2)
            // Creating a Gen2 VM with -BootDevice CD plus a disk fails (both claim SCSI 0:0): add the drive afterwards.
            s.Cmd("$__dvd = Add-VMDvdDrive", ("VM", VmRef), ("Path", PsValue.Raw("$__iso")), ("Passthru", PsValue.Of(true)))
                .Cmd("Set-VMFirmware", ("VM", VmRef), ("FirstBootDevice", PsValue.Raw("$__dvd")));
        else if (source == "iso")
            // Generation 1 VMs get an IDE DVD drive from New-VM and boot from CD first by default.
            s.Line("$__dvd = @(Get-VMDvdDrive -VM $vm) | Select-Object -First 1")
                .Line("if ($null -eq $__dvd) { " + Render("Add-VMDvdDrive", ("VM", VmRef), ("Path", PsValue.Raw("$__iso"))) + " } else { "
                    + Render("Set-VMDvdDrive", ("VMDvdDrive", PsValue.Raw("$__dvd")), ("Path", PsValue.Raw("$__iso"))) + " }");
        if (source == "linked")
            s.Line("Set-ControlReadOnly $__parent $true")
                .Line("[void]$__warnings.Add('Marked the parent ' + $__parent + ' read-only: changing it would break this VM''s differencing disk.')");
        s.Line("} catch {")
            .Line("$__failure = $_")
            .Line("if ($null -ne $vm) { try { Remove-VM -VM $vm -Force } catch { } }")
            .Line("foreach ($__file in @($__created)) { try { if ([System.IO.File]::Exists($__file)) { [System.IO.File]::Delete($__file) } } catch { } }")
            .Line("throw $__failure")
            .Line("}");
        if (r.Start)
            s.Line("try { Start-VM -VM $vm } catch { [void]$__warnings.Add('The VM was created but did not start: ' + $_.Exception.Message) }");
        if (tpm) s.Line("[void]$__warnings.Add('The vTPM uses a local key protector: the VM starts only on hosts that hold this host''s Shielded VM guardian certificates.')");
        if (gen2 && !secureBoot) s.Line("[void]$__warnings.Add('Secure Boot is off.')");

        s.Line(Refresh)
            .Line("$__result = [ordered]@{}")
            .Line("foreach ($__property in (ConvertTo-ControlVm $vm).PSObject.Properties) { $__result[$__property.Name] = $__property.Value }")
            .Line("$__result.Firmware = (ConvertTo-ControlFirmware $vm)")
            .Line("$__result.Security = (ConvertTo-ControlSecurity $vm)")
            .Line("$__result.createdFiles = @($__created)")
            .Line("$__result.warnings = @($__warnings)")
            .Line("$__result.nextSteps = " + Array(CreateNextSteps(source, r.Start)));
        return runner.InvokeAsync(s.Returns("$__result"), ct, 600);
    }

    private static IEnumerable<string> CreateNextSteps(string source, bool started)
    {
        switch (source)
        {
            case "new_disk":
                yield return "The new disk is empty: attach installation media with hyperv_disk action=attach_iso and set hyperv_firmware firstBootDevice=dvd (or create with source=iso) before starting.";
                break;
            case "iso":
                yield return started
                    ? "Open hyperv_console now and press a key: Windows setup ISOs wait only a few seconds at 'Press any key to boot from CD or DVD' before falling through to the empty disk."
                    : "Start with hyperv_power action=start, then open hyperv_console at once and press a key: Windows setup ISOs wait only a few seconds at 'Press any key to boot from CD or DVD'.";
                break;
            case "none":
                yield return "The VM has no disk: create one with hyperv_vhd action=create and attach it with hyperv_disk action=attach_vhd before starting.";
                break;
            default:
                if (!started) yield return "Start it with hyperv_power action=start; it boots from its disk.";
                break;
        }
        yield return "Inspect the result with hyperv_vm_info; change hardware with hyperv_vm_configure, hyperv_firmware, hyperv_disk and hyperv_network.";
    }

    /// <summary>
    /// Removes a VM that is Off or Saved; never powers it off. With <paramref name="deleteDisks"/>, its
    /// checkpoints are removed first and their merges awaited, then the VM, then the disk files no other
    /// VM or differencing child uses. <paramref name="dryRun"/> runs a script with no mutation at all.
    /// </summary>
    public Task<JsonElement> RemoveAsync(string machine, bool deleteDisks, bool dryRun, CancellationToken ct)
    {
        var s = Script().Vm(machine).RequireState(VmStateRules.StateNames("remove"))
            .Line("$__warnings = New-Object System.Collections.ArrayList")
            .Line("$__checkpoints = @(Get-VMSnapshot -VM $vm)");
        if (deleteDisks)
            s.Line("if ($__checkpoints.Count -gt 0 -and [string]$vm.State -ne 'Off') { Stop-Control 'InvalidVmState' (\"VM '\" + $vm.Name + \"' is \" + $vm.State + ' and has ' + $__checkpoints.Count + ' checkpoint(s). Deleting its disks first removes the checkpoints and waits for their merge, which needs the VM Off. Turn it off (this discards the saved state) or remove it without deleteDisks. Nothing was changed.') }");
        else
            s.Line("if ($__checkpoints.Count -gt 0) { [void]$__warnings.Add('Hyper-V merges this VM''s checkpoint files (.avhdx) into their parent disks in the background after removal; do not move or delete those disks until the merge finishes.') }");
        s.Line($"$__plan = Get-ControlRemovalPlan $vm {B(deleteDisks)}");
        if (dryRun)
            return runner.InvokeAsync(s.Returns($"[ordered]@{{ success = $true; dryRun = $true; deleteDisks = {B(deleteDisks)}; vm = [ordered]@{{ id = [string]$vm.Id; name = [string]$vm.Name; state = [string]$vm.State }}; plan = $__plan; warnings = @($__warnings) }}"), ct, 300);

        s.Line("$__removedCheckpoints = 0").Line("$__merged = @($__plan.merge)");
        if (deleteDisks)
            s.Line("if ($__checkpoints.Count -gt 0) {")
                .Line("foreach ($__checkpoint in @($__checkpoints | Where-Object { -not $_.ParentSnapshotId })) { " + Render("Remove-VMSnapshot", ("VMSnapshot", PsValue.Raw("$__checkpoint")), ("IncludeAllChildSnapshots", PsValue.Of(true))) + " }")
                .Line("foreach ($__checkpoint in @(Get-VMSnapshot -VM $vm)) { " + Render("Remove-VMSnapshot", ("VMSnapshot", PsValue.Raw("$__checkpoint"))) + " }")
                .Line("$__removedCheckpoints = $__checkpoints.Count")
                .Line("$vm = Wait-ControlMerge $vm 1500")
                .Line("$__plan = Get-ControlRemovalPlan $vm $true")
                .Line("}");
        s.Line("$__id = [string]$vm.Id; $__vmName = [string]$vm.Name; $__vmPath = [string]$vm.Path")
            .Cmd("Remove-VM", ("VM", VmRef), ("Force", PsValue.Of(true)))
            .Line("$__deleted = New-Object System.Collections.ArrayList")
            .Line("$__kept = New-Object System.Collections.ArrayList")
            .Line("foreach ($__entry in @($__plan.keep)) { [void]$__kept.Add($__entry) }")
            .Line("$__removedDirectories = @()");
        if (deleteDisks)
            s.Line("""
                $__after = Get-ControlDiskReferences $null
                $__targets = @($__plan.delete) + @($__merged | Where-Object { [System.IO.File]::Exists($_) })
                foreach ($__file in $__targets) {
                    $__reason = Get-ControlReferenceReason $__after (Get-ControlPathKey $__file)
                    if ($null -ne $__reason) { [void]$__kept.Add([pscustomobject][ordered]@{ path = $__file; reason = $__reason }); continue }
                    try {
                        if ([System.IO.File]::Exists($__file)) { Set-ControlReadOnly $__file $false; Microsoft.PowerShell.Management\Remove-Item -LiteralPath $__file -Force -ErrorAction Stop }
                        if ([System.IO.File]::Exists($__file)) { [void]$__kept.Add([pscustomobject][ordered]@{ path = $__file; reason = 'still present after deletion' }) }
                        else { [void]$__deleted.Add($__file) }
                    } catch { [void]$__kept.Add([pscustomobject][ordered]@{ path = $__file; reason = 'deletion failed: ' + $_.Exception.Message }) }
                }
                $__hostPaths = Get-VMHost
                $__removedDirectories = @(Remove-ControlVmDirectory $__vmPath $__vmName @([string]$__hostPaths.VirtualMachinePath, [string]$__hostPaths.VirtualHardDiskPath) | Where-Object { $null -ne $_ })
                """);
        return runner.InvokeAsync(s.Returns($"[ordered]@{{ success = $true; dryRun = $false; deleteDisks = {B(deleteDisks)}; vm = [ordered]@{{ id = $__id; name = $__vmName }}; checkpointsRemoved = $__removedCheckpoints; deleted = @($__deleted); kept = @($__kept); removedDirectories = $__removedDirectories; warnings = @($__warnings) }}"), ct, 1800);
    }

    public Task<JsonElement> ConfigureAsync(string machine, int? processors, int? memoryMb, bool? dynamicMemory, string? checkpointType, string? newName, CancellationToken ct) =>
        ConfigureAsync(machine, new VmConfigureRequest { Processors = processors, MemoryMb = memoryMb, DynamicMemory = dynamicMemory, CheckpointType = checkpointType, NewName = newName }, ct);

    /// <summary>
    /// Applies every requested setting the VM's current state allows, or refuses the whole request
    /// before any change, listing the settings that need the VM Off. Returns the refreshed projection.
    /// </summary>
    public Task<JsonElement> ConfigureAsync(string machine, VmConfigureRequest r, CancellationToken ct)
    {
        if (r.Processors is < 1 or > 2048) throw new ArgumentException("processors must be 1-2048.");
        if (r.MemoryMb is { } startup) Memory(startup, "memoryMb");
        if (r.MemoryMinMb is { } minimum) Memory(minimum, "memoryMinMb");
        if (r.MemoryMaxMb is { } maximum) Memory(maximum, "memoryMaxMb");
        if (r.MemoryBufferPercent is < 5 or > 2000) throw new ArgumentException("memoryBufferPercent must be 5-2000.");
        if (r.AutoStartDelaySeconds is < 0) throw new ArgumentException("autoStartDelaySeconds must be 0 or more.");
        if (r.NewName is not null) VmName(r.NewName, "newName");
        if (r.CheckpointType is not null) Choice(r.CheckpointType, CheckpointTypes, "checkpointType");
        if (r.AutoStartAction is not null) Choice(r.AutoStartAction, StartActions, "autoStartAction");
        if (r.AutoStopAction is not null) Choice(r.AutoStopAction, StopActions, "autoStopAction");
        if (r.EnhancedSessionTransport is not null) Choice(r.EnhancedSessionTransport, SessionTransports, "enhancedSessionTransport");
        if (r.IntegrationServices is { } map && map.Keys.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("integrationServices names must not be empty.");

        PsValue? Mb(int? value) => value is null ? null : PsValue.Megabytes(value.Value);
        var requested = new List<(string, PsValue?)>();
        void Want(bool present, string setting, PsValue? value = null) { if (present) requested.Add((setting, value)); }
        Want(r.Processors is not null, "processors");
        Want(r.MemoryMb is not null, "memoryMb", Mb(r.MemoryMb));
        Want(r.DynamicMemory is not null, "dynamicMemory");
        Want(r.MemoryMinMb is not null, "memoryMinMb", Mb(r.MemoryMinMb));
        Want(r.MemoryMaxMb is not null, "memoryMaxMb", Mb(r.MemoryMaxMb));
        Want(r.MemoryBufferPercent is not null, "memoryBufferPercent");
        Want(r.CheckpointType is not null, "checkpointType");
        Want(r.AutoCheckpoints is not null, "autoCheckpoints");
        Want(r.NewName is not null, "newName");
        Want(r.Notes is not null, "notes");
        Want(r.AutoStartAction is not null, "autoStartAction");
        Want(r.AutoStartDelaySeconds is not null, "autoStartDelaySeconds");
        Want(r.AutoStopAction is not null, "autoStopAction");
        Want(r.NestedVirtualization is not null, "nestedVirtualization");
        Want(r.IntegrationServices is { Count: > 0 }, "integrationServices");
        Want(r.EnhancedSessionTransport is not null, "enhancedSessionTransport");

        var s = Script().Vm(machine).Line(VmStateRules.Gate(requested));

        // Validation against the VM's current values; still before any change.
        var dynamicBounds = r.MemoryMinMb is not null || r.MemoryMaxMb is not null || r.MemoryBufferPercent is not null;
        if (dynamicBounds || r.MemoryMb is not null || r.DynamicMemory is not null)
        {
            s.Line("$__dynamic = " + (r.DynamicMemory is { } dynamic ? B(dynamic) : "[bool]$vm.DynamicMemoryEnabled"))
                .Line("$__startup = " + (Mb(r.MemoryMb)?.Text ?? "[long]$vm.MemoryStartup"))
                .Line("$__minimum = " + (Mb(r.MemoryMinMb)?.Text ?? "[long]$vm.MemoryMinimum"))
                .Line("$__maximum = " + (Mb(r.MemoryMaxMb)?.Text ?? "[long]$vm.MemoryMaximum"));
            if (dynamicBounds)
                s.Line("if (-not $__dynamic) { Stop-Control 'InvalidArgument' 'memoryMinMb, memoryMaxMb and memoryBufferPercent apply only with dynamic memory: pass dynamicMemory=true or enable it first. Nothing was changed.' }");
            s.Line("if ($__dynamic -and ($__minimum -gt $__startup -or $__startup -gt $__maximum)) { Stop-Control 'InvalidArgument' ('Dynamic memory needs minimum <= startup <= maximum; this request gives ' + ($__minimum / 1MB) + ' / ' + ($__startup / 1MB) + ' / ' + ($__maximum / 1MB) + ' MB. Nothing was changed.') }");
        }
        var services = r.IntegrationServices?.ToList() ?? [];
        if (services.Count > 0)
        {
            s.Line("$__services = @(Get-VMIntegrationService -VM $vm)");
            for (var i = 0; i < services.Count; i++)
            {
                var name = services[i].Key.Trim();
                var id = IntegrationServiceIds.TryGetValue(name, out var known) ? known : Guid.TryParse(name, out var guid) ? guid.ToString("D").ToUpperInvariant() : null;
                var match = (id is null ? "" : $"([string]$_.Id).EndsWith('\\{id}', [System.StringComparison]::OrdinalIgnoreCase) -or ") + "$_.Name -eq " + L(name);
                s.Line($"$__service{i} = @($__services | Where-Object {{ {match} }})")
                    .Line($"if ($__service{i}.Count -ne 1) {{ Stop-Control 'InvalidArgument' ('Integration service ' + {L(L(name))} + ' not found. This VM has: ' + (($__services | ForEach-Object {{ $_.Name }}) -join ', ') + '. Nothing was changed.') }}");
            }
        }

        if (r.Processors is not null || r.NestedVirtualization is not null)
            s.Cmd("Set-VMProcessor", ("VM", VmRef), ("Count", r.Processors is { } count ? PsValue.Of(count) : null),
                ("ExposeVirtualizationExtensions", r.NestedVirtualization is { } nested ? PsValue.Of(nested) : null));
        if (r.MemoryMb is not null || r.DynamicMemory is not null || dynamicBounds)
            s.Cmd("Set-VMMemory", ("VM", VmRef), ("DynamicMemoryEnabled", r.DynamicMemory is { } dm ? PsValue.Of(dm) : null),
                ("StartupBytes", Mb(r.MemoryMb)), ("MinimumBytes", Mb(r.MemoryMinMb)), ("MaximumBytes", Mb(r.MemoryMaxMb)),
                ("Buffer", r.MemoryBufferPercent is { } buffer ? PsValue.Of(buffer) : null));
        if (r.CheckpointType is not null || r.AutoCheckpoints is not null || r.Notes is not null || r.AutoStartAction is not null
            || r.AutoStartDelaySeconds is not null || r.AutoStopAction is not null || r.EnhancedSessionTransport is not null)
            s.Cmd("Set-VM", ("VM", VmRef),
                ("CheckpointType", r.CheckpointType is null ? null : PsValue.Token(r.CheckpointType, CheckpointTypes)),
                ("AutomaticCheckpointsEnabled", r.AutoCheckpoints is { } auto ? PsValue.Of(auto) : null),
                ("Notes", r.Notes is null ? null : PsValue.Of(r.Notes)),
                ("AutomaticStartAction", r.AutoStartAction is null ? null : PsValue.Token(r.AutoStartAction, StartActions)),
                ("AutomaticStartDelay", r.AutoStartDelaySeconds is { } delay ? PsValue.Of(delay) : null),
                ("AutomaticStopAction", r.AutoStopAction is null ? null : PsValue.Token(r.AutoStopAction, StopActions)),
                ("EnhancedSessionTransportType", r.EnhancedSessionTransport is null ? null : PsValue.Token(r.EnhancedSessionTransport, SessionTransports)));
        for (var i = 0; i < services.Count; i++)
            s.Cmd(services[i].Value ? "Enable-VMIntegrationService" : "Disable-VMIntegrationService", ("VM", VmRef), ("Name", PsValue.Raw($"$__service{i}[0].Name")));
        if (r.NewName is not null) s.Cmd("Rename-VM", ("VM", VmRef), ("NewName", PsValue.Of(r.NewName)));
        return runner.InvokeAsync(s.Line(Refresh).Returns(Projection), ct);
    }

    public Task<JsonElement> ExportAsync(string machine, string destination, CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Vm(machine).Cmd("Export-VM", ("VM", VmRef), ("Path", PsValue.Of(ControlService.FullPath(destination)))).Returns(Success), ct, 1800);

    public Task<JsonElement> ImportAsync(string configurationPath, string vmDestination, string vhdDestination, CancellationToken ct) => runner.InvokeAsync(
        Script().Cmd("$vm = Import-VM",
            ("Path", PsValue.Of(ControlService.FullPath(configurationPath))), ("Copy", PsValue.Of(true)), ("GenerateNewId", PsValue.Of(true)),
            ("VirtualMachinePath", PsValue.Of(ControlService.FullPath(vmDestination))), ("VhdDestinationPath", PsValue.Of(ControlService.FullPath(vhdDestination))))
        .Returns(Projection), ct, 1800);

    public Task<JsonElement> PowerResetAsync(string machine, CancellationToken ct) => runner.InvokeAsync(Script().Vm(machine)
        .Cmd("Restart-VM", ("VM", VmRef), ("Force", PsValue.Of(true)), ("Confirm", PsValue.Of(false)))
        .Line(Refresh).Returns(Projection), ct);

    public Task<JsonElement> RenameCheckpointAsync(Guid machineId, Guid checkpointId, string newName, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machineId)
        .Line($"Get-VMSnapshot -VM $vm | Where-Object {{ $_.Id -eq ([guid]'{checkpointId:D}') }} | Rename-VMSnapshot -NewName {L(newName)}")
        .Returns(Success), ct);

    /// <summary>
    /// Maps a COM port to a named pipe (Off or Running; verified live) and reads the port back. A Generation 2
    /// guest detects a newly mapped port only after a full stop and start, which the result notes.
    /// </summary>
    public Task<JsonElement> SetComPortAsync(Guid machineId, int number, string pipePath, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machineId)
        .RequireState(VmStateRules.StateNames("comPort"))
        .Cmd("Set-VMComPort", ("VM", VmRef), ("Number", PsValue.Of(number)), ("Path", PsValue.Of(pipePath)))
        .Line($"$__port = Get-VMComPort -VM $vm -Number {number}")
        .Line("$__note = $null")
        .Line("if ([string]$vm.State -eq 'Running' -and [int]$vm.Generation -eq 2) { $__note = 'Mapped while Running: a Generation 2 guest detects a newly mapped COM port only after the VM is turned off and started again, not after a guest restart.' }")
        .Returns("[ordered]@{ Name = [string]$__port.Name; Path = [string]$__port.Path; Note = $__note }"), ct);
}
