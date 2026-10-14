using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using static HyperVControl.Services.VmStateRules;

namespace HyperVControl.Tests;

/// <summary>
/// Lifecycle v2 against the recording mock: the setting x state table and its in-script gate (a refusal
/// names every blocked setting and nothing changes first), create sources and their cmdlet order, removal
/// plans and guards, firmware/vTPM order, media, network, virtual disk and clone operations. Disk files are
/// real empty files in a temporary folder; no Hyper-V host is touched.
/// </summary>
internal static class LifecycleV2Tests
{
    private const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";
    private static readonly string[] MutatingVerbs = ["Set", "New", "Remove", "Add", "Enable", "Disable", "Rename", "Connect", "Disconnect", "Resize",
        "Start", "Stop", "Suspend", "Resume", "Restart", "Export", "Import", "Checkpoint", "Restore", "Update", "Merge", "Mount", "Dismount"];

    internal static async Task RunAsync(Action<bool, string> check)
    {
        StateTable(check);
        await ConfigureAsync(check);
        await CreateAsync(check);
        await RemoveAsync(check);
        await FirmwareAsync(check);
        await NetworkAsync(check);
        await MediaAsync(check);
        await VhdAsync(check);
        await CloneAndInfoAsync(check);
    }

    private static void StateTable(Action<bool, string> check)
    {
        check(Table["processors"].Allowed == States.Off && Table["dynamicMemory"].Allowed == States.Off && Table["memoryMb"].Allowed == (States.Off | States.Running)
            && Table["memoryMb"].WhileRunning is not null && Table["memoryMaxMb"].WhileRunning!.Expression.Contains("{value} -ge"),
            "State table: vCPU count and dynamic memory toggle are Off-only; static memory resize is live with a condition; the maximum may only grow");
        check(new[] { "secureBoot", "secureBootTemplate", "tpm", "firstBootDevice", "staticMac", "autoStopAction", "nestedVirtualization", "enhancedSessionTransport", "clone" }
                .All(setting => Table[setting].Allowed == States.Off)
            && new[] { "notes", "autoStartAction", "integrationServices", "connect", "macSpoofing", "vlanId", "eject_iso", "comPort" }.All(setting => Table[setting].Allowed == (States.Off | States.Running))
            && Table["add_adapter"].WhileRunning is not null && Table["detach_vhd"].WhileRunning is not null,
            "State table: firmware, TPM, MAC, stop action, nested virtualization and clone sources need Off; notes, start action, integration services, VLAN and ISO eject are live");
        check(Table["newName"].Allows(States.Saved) && !Table["notes"].Allows(States.Saved) && Table["remove"].Allowed == (States.Off | States.Saved)
            && Table.Values.All(rule => !rule.Allows(States.None)),
            "State table: Saved allows rename and removal only; nothing is allowed in Paused, transitional or critical states");
        check(Blocked("Running", ["processors", "notes", "memoryMb", "dynamicMemory"]).SequenceEqual(["processors", "dynamicMemory"])
            && Blocked("Paused", ["notes"]).Count == 1 && Blocked("Saved", ["newName"]).Count == 0 && Blocked("Off", Table.Keys).Count == 0
            && Parse("FastSaved") == States.Saved && Parse("RunningCritical") == States.None && Parse("Starting") == States.None,
            "State evaluation lists exactly the settings a state rules out");
        check(Gate([]) == "" && Gate([("memoryMaxMb", PsValue.Megabytes(4096))]).Contains("([long]4096*1MB) -ge [long]$vm.MemoryMaximum")
            && Gate([("notes", null)]).Contains("states = @('Off', 'Running')"),
            "The gate renders requested values into Running conditions and quotes every state");
    }

    private static async Task ConfigureAsync(Action<bool, string> check)
    {
        var running = Vm("Run Static", "Running", ("memoryStartup", 2L << 30));
        var dynamic = Vm("Run Dynamic", "Running", ("dynamicMemory", true), ("memoryMinimum", 512L << 20), ("memoryMaximum", 8L << 30));
        var off = Vm("Off One", "Off");
        var offStatic = Vm("Off Two", "Off");
        var paused = Vm("Paused One", "Paused");
        var saved = Vm("Saved One", "Saved");
        using var mock = new MockHyperV(new { vms = new object[] { running, dynamic, off, offStatic, paused, saved }, switches = Switches() });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        var refused = await Expect(() => lifecycle.ConfigureAsync("Run Static", new VmConfigureRequest { Processors = 4, DynamicMemory = true, MemoryMb = 3072, Notes = "n" }, default));
        check(refused?.ErrorType == "InvalidVmState" && refused.Message.Contains("processors (") && refused.Message.Contains("dynamicMemory (")
            && !refused.Message.Contains("memoryMb") && !refused.Message.Contains("notes") && refused.Message.Contains("Nothing was changed") && !Mutated(mock),
            "Running: the refusal names exactly the settings that need Off (vCPUs, dynamic memory toggle) and nothing changes first");

        mock.ClearCalls();
        var live = await lifecycle.ConfigureAsync("Run Static", new VmConfigureRequest
        {
            MemoryMb = 3072, Notes = "lab notes", AutoStartAction = "Start", AutoStartDelaySeconds = 30, AutoCheckpoints = false, CheckpointType = "Standard",
            IntegrationServices = new Dictionary<string, bool> { ["Guest Service Interface"] = true, ["vss"] = false }
        }, default);
        var memory = Single(mock, "Set-VMMemory");
        var setVm = Single(mock, "Set-VM");
        check(memory.GetProperty("StartupBytes").GetInt64() == 3072L << 20 && !memory.TryGetProperty("DynamicMemoryEnabled", out _)
            && setVm.GetProperty("Notes").GetString() == "lab notes" && setVm.GetProperty("AutomaticStartAction").GetString() == "Start"
            && setVm.GetProperty("AutomaticStartDelay").GetInt32() == 30 && !setVm.GetProperty("AutomaticCheckpointsEnabled").GetBoolean()
            && Single(mock, "Enable-VMIntegrationService").GetProperty("Name").GetString() == "Guest Service Interface"
            && Single(mock, "Disable-VMIntegrationService").GetProperty("Name").GetString() == "VSS"
            && live.GetProperty("Notes").GetString() == "lab notes" && live.GetProperty("MemoryStartup").GetInt64() == 3072L << 20
            && live.GetProperty("IntegrationServices").GetProperty("Guest Service Interface").GetBoolean() && !live.GetProperty("IntegrationServices").GetProperty("VSS").GetBoolean(),
            "Running: static memory resize, notes, start action, checkpoints and integration services (matched by locale-independent ID) apply live");

        mock.ClearCalls();
        var shrink = await Expect(() => lifecycle.ConfigureAsync("Run Dynamic", new VmConfigureRequest { MemoryMaxMb = 4096 }, default));
        var startup = await Expect(() => lifecycle.ConfigureAsync("Run Dynamic", new VmConfigureRequest { MemoryMb = 4096 }, default));
        check(shrink?.ErrorType == "InvalidVmState" && shrink.Message.Contains("memoryMaxMb") && shrink.Message.Contains("only increase")
            && startup?.ErrorType == "InvalidVmState" && startup.Message.Contains("only with static memory") && !Mutated(mock),
            "Running with dynamic memory: lowering the maximum or changing startup memory is refused before any change");

        mock.ClearCalls();
        await lifecycle.ConfigureAsync("Run Dynamic", new VmConfigureRequest { MemoryMaxMb = 16384, MemoryMinMb = 256, MemoryBufferPercent = 30 }, default);
        var bounds = Single(mock, "Set-VMMemory");
        check(bounds.GetProperty("MaximumBytes").GetInt64() == 16L << 30 && bounds.GetProperty("MinimumBytes").GetInt64() == 256L << 20 && bounds.GetProperty("Buffer").GetInt32() == 30,
            "Running with dynamic memory: a larger maximum, smaller minimum and buffer apply in one Set-VMMemory call");

        mock.ClearCalls();
        var pausedRefusal = await Expect(() => lifecycle.ConfigureAsync("Paused One", new VmConfigureRequest { Notes = "x" }, default));
        var savedRefusal = await Expect(() => lifecycle.ConfigureAsync("Saved One", new VmConfigureRequest { Notes = "x", NewName = "Saved Renamed" }, default));
        check(pausedRefusal?.ErrorType == "InvalidVmState" && pausedRefusal.Message.Contains("Resume") && savedRefusal?.ErrorType == "InvalidVmState"
            && savedRefusal.Message.Contains("saved VM accepts only") && savedRefusal.Message.Contains("notes (") && !savedRefusal.Message.Contains("newName") && !Mutated(mock),
            "Paused VMs accept nothing; a Saved VM refuses notes while allowing rename, all-or-nothing");
        await lifecycle.ConfigureAsync("Saved One", new VmConfigureRequest { NewName = "Saved Renamed" }, default);
        check(Single(mock, "Rename-VM").GetProperty("NewName").GetString() == "Saved Renamed", "A Saved VM accepts a rename");

        mock.ClearCalls();
        var applied = await lifecycle.ConfigureAsync("Off One", new VmConfigureRequest
        {
            Processors = 8, DynamicMemory = true, MemoryMinMb = 1024, MemoryMaxMb = 8192, MemoryMb = 2048, AutoStopAction = "ShutDown",
            NestedVirtualization = true, EnhancedSessionTransport = "HvSocket"
        }, default);
        var processor = Single(mock, "Set-VMProcessor");
        var offMemory = Single(mock, "Set-VMMemory");
        var offVm = Single(mock, "Set-VM");
        check(processor.GetProperty("Count").GetInt64() == 8 && processor.GetProperty("ExposeVirtualizationExtensions").GetBoolean()
            && offMemory.GetProperty("DynamicMemoryEnabled").GetBoolean() && offMemory.GetProperty("StartupBytes").GetInt64() == 2048L << 20
            && offMemory.GetProperty("MinimumBytes").GetInt64() == 1024L << 20 && offMemory.GetProperty("MaximumBytes").GetInt64() == 8192L << 20
            && offVm.GetProperty("AutomaticStopAction").GetString() == "ShutDown" && offVm.GetProperty("EnhancedSessionTransportType").GetString() == "HvSocket"
            && applied.GetProperty("DynamicMemoryEnabled").GetBoolean() && applied.GetProperty("NestedVirtualization").GetBoolean() && applied.GetProperty("ProcessorCount").GetInt32() == 8,
            "Off: vCPUs, dynamic memory with bounds, stop action, nested virtualization and session transport apply and read back");

        mock.ClearCalls();
        var noDynamic = await Expect(() => lifecycle.ConfigureAsync("Off Two", new VmConfigureRequest { MemoryMinMb = 1024 }, default));
        var badOrder = await Expect(() => lifecycle.ConfigureAsync("Off Two", new VmConfigureRequest { DynamicMemory = true, MemoryMb = 4096, MemoryMaxMb = 2048 }, default));
        var unknownService = await Expect(() => lifecycle.ConfigureAsync("Off Two", new VmConfigureRequest { Notes = "x", IntegrationServices = new Dictionary<string, bool> { ["Nope"] = true } }, default));
        check(noDynamic?.ErrorType == "InvalidArgument" && badOrder?.ErrorType == "InvalidArgument" && badOrder.Message.Contains("minimum <= startup <= maximum")
            && unknownService?.ErrorType == "InvalidArgument" && unknownService.Message.Contains("Heartbeat") && !Mutated(mock),
            "Invalid memory bounds and unknown integration services are refused against the VM's current values before any change");
        check(await Throws<ArgumentException>(() => lifecycle.ConfigureAsync("Off Two", new VmConfigureRequest { MemoryMb = 33 }, default))
            && await Throws<ArgumentException>(() => lifecycle.ConfigureAsync("Off Two", new VmConfigureRequest { AutoStartAction = "Always" }, default)),
            "Odd memory sizes and unknown actions fail validation before PowerShell runs");
    }

    private static async Task CreateAsync(Action<bool, string> check)
    {
        using var box = new Sandbox();
        var existing = Vm("Existing", "Off", ("disks", new[] { box.File(@"VHDs\existing.vhdx") }));
        var parent = box.File(@"parents\parent.vhdx");
        var loose = box.File(@"loose\loose.vhdx");
        var iso = box.File(@"iso\setup.iso");
        using var mock = new MockHyperV(new
        {
            host = Host(box), vms = new object[] { existing }, switches = Switches(),
            vhds = new object[] { Vhd(parent, size: 10L << 30) }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        var defaults = new VmCreateRequest("x");
        check(defaults is { Source: "new_disk", Generation: 2, MemoryMb: 4096, Processors: 2, DiskGb: 64, OsFamily: "windows", CheckpointType: "Standard", AutoCheckpoints: false, Start: false, AllowDuplicateName: false }
            && defaults.SecureBoot is null && defaults.Tpm is null,
            "Create defaults: new 64 GB disk, Generation 2, 4096 MB static, 2 vCPUs, Standard checkpoints without automatic ones, stays Off");

        mock.ClearCalls();
        var created = await lifecycle.CreateAsync(new VmCreateRequest("Win A") { DiskGb = 2 }, default);
        var disk = box.P(@"VHDs\Win A.vhdx");
        var newVm = Single(mock, "New-VM");
        check(newVm.GetProperty("Generation").GetInt32() == 2 && newVm.GetProperty("MemoryStartupBytes").GetInt64() == 4096L << 20
            && newVm.GetProperty("NewVHDPath").GetString() == disk && newVm.GetProperty("NewVHDSizeBytes").GetInt64() == 2L << 30
            && newVm.GetProperty("Path").GetString() == box.P("VMs") && newVm.GetProperty("SwitchName").GetString() == "Default Switch" && !newVm.TryGetProperty("BootDevice", out _)
            && Single(mock, "Set-VMProcessor").GetProperty("Count").GetInt64() == 2
            && Single(mock, "Set-VM").GetProperty("CheckpointType").GetString() == "Standard" && !Single(mock, "Set-VM").GetProperty("AutomaticCheckpointsEnabled").GetBoolean()
            && File.Exists(disk) && created.GetProperty("createdFiles").EnumerateArray().Single().GetString() == disk && created.GetProperty("State").GetString() == "Off"
            && Single(mock, "Set-VMMemory").GetProperty("DynamicMemoryEnabled").ValueKind == JsonValueKind.False && !Single(mock, "Set-VMMemory").TryGetProperty("MinimumBytes", out _)
            && !created.GetProperty("DynamicMemoryEnabled").GetBoolean(),
            "Create new_disk: default paths from Get-VMHost, Default Switch by ID, typed New-VM, static memory set explicitly (New-VM enables dynamic memory), CPU and checkpoint settings; the disk is reported as created");
        var template = First(mock, "Set-VMFirmware", p => p.TryGetProperty("SecureBootTemplate", out _));
        var secureBoot = First(mock, "Set-VMFirmware", p => p.TryGetProperty("EnableSecureBoot", out _));
        var protector = First(mock, "Set-VMKeyProtector", p => Flag(p, "NewLocalKeyProtector"));
        var tpm = First(mock, "Enable-VMTPM");
        check(First(mock, "New-VM") < template && template < secureBoot && secureBoot < protector && protector < tpm
            && Single(mock, "Set-VMFirmware", p => p.TryGetProperty("SecureBootTemplate", out _)).GetProperty("SecureBootTemplate").GetString() == "MicrosoftWindows"
            && created.GetProperty("Security").GetProperty("TpmEnabled").GetBoolean() && created.GetProperty("Firmware").GetProperty("SecureBoot").GetString() == "On"
            && Texts(created, "warnings").Any(w => w.Contains("key protector")) && Texts(created, "nextSteps").Any(s => s.Contains("attach_iso")),
            "Create windows: Secure Boot template before Secure Boot, then a local key protector before Enable-VMTPM; warnings and next steps say why");
        check(First(mock, "Set-VMFirmware", p => Str(p, "FirstBootDevice") == disk) > tpm
            && created.GetProperty("Firmware").GetProperty("BootOrder")[0].GetProperty("Device").GetString() == disk,
            "Create puts the disk ahead of the network adapter that New-VM boots first");

        mock.ClearCalls();
        await lifecycle.CreateAsync(new VmCreateRequest("Win B") { DiskGb = 2, Path = box.P("custom") }, default);
        check(Single(mock, "New-VM").GetProperty("NewVHDPath").GetString() == box.P(@"custom\Win B\Virtual Hard Disks\Win B.vhdx") && Single(mock, "New-VM").GetProperty("Path").GetString() == box.P("custom"),
            "Create with path: the VM folder is <path>\\<name> and its disk goes to <path>\\<name>\\Virtual Hard Disks");

        mock.ClearCalls();
        var duplicate = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("Existing") { DiskGb = 2 }, default));
        check(duplicate?.ErrorType == "InvalidArgument" && duplicate.Message.Contains("allowDuplicateName") && !Mutated(mock), "Create refuses an existing VM name before any change");
        var twin = await lifecycle.CreateAsync(new VmCreateRequest("Existing") { Source = "none", AllowDuplicateName = true }, default);
        check(Single(mock, "New-VM").GetProperty("NoVHD").GetBoolean() && !Single(mock, "New-VM").TryGetProperty("NewVHDPath", out _)
            && Texts(twin, "warnings").Any(w => w.Contains("same name") || w.Contains("this name")) && twin.GetProperty("createdFiles").GetArrayLength() == 0,
            "Create none with allowDuplicateName: New-VM -NoVHD and a warning to use the ID");

        mock.ClearCalls();
        await lifecycle.CreateAsync(new VmCreateRequest("Iso A") { Source = "iso", IsoPath = iso, DiskGb = 2 }, default);
        var isoNew = Single(mock, "New-VM");
        var dvd = First(mock, "Add-VMDvdDrive", p => Str(p, "Path") == iso && Flag(p, "Passthru"));
        var firstBoot = First(mock, "Set-VMFirmware", p => Str(p, "FirstBootDevice") == "DVD");
        check(isoNew.TryGetProperty("NewVHDPath", out _) && !isoNew.TryGetProperty("BootDevice", out _) && First(mock, "New-VM") < dvd && dvd < firstBoot
            && First(mock, "Enable-VMTPM") < dvd && First(mock, "Set-VMFirmware", p => Str(p, "FirstBootDevice") == box.P(@"VHDs\Iso A.vhdx")) is >= 0 and var diskFirst && diskFirst < firstBoot,
            "Create iso: New-VM with the disk and no boot device, then Add-VMDvdDrive, then the DVD as first boot device ahead of the disk");

        mock.ClearCalls();
        await lifecycle.CreateAsync(new VmCreateRequest("Attach A") { Source = "existing_vhd", VhdPath = loose }, default);
        check(Single(mock, "New-VM").GetProperty("VHDPath").GetString() == loose && !Single(mock, "New-VM").TryGetProperty("NewVHDPath", out _),
            "Create existing_vhd attaches the given disk");
        mock.ClearCalls();
        var taken = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("Attach B") { Source = "existing_vhd", VhdPath = box.P(@"VHDs\existing.vhdx") }, default));
        check(taken?.ErrorType == "InvalidArgument" && taken.Message.Contains("attached to VM 'Existing'") && !Mutated(mock), "Create refuses a disk another VM attaches");

        mock.ClearCalls();
        var linked = await lifecycle.CreateAsync(new VmCreateRequest("Linked A") { Source = "linked", ParentPath = parent }, default);
        var child = box.P(@"VHDs\Linked A.vhdx");
        var newVhd = First(mock, "New-VHD", p => Str(p, "Path") == child && Str(p, "ParentPath") == parent && Flag(p, "Differencing"));
        check(newVhd >= 0 && newVhd < First(mock, "New-VM") && Single(mock, "New-VM").GetProperty("VHDPath").GetString() == child
            && File.GetAttributes(parent).HasFlag(FileAttributes.ReadOnly) && Texts(linked, "warnings").Any(w => w.Contains("read-only"))
            && linked.GetProperty("createdFiles").EnumerateArray().Single().GetString() == child,
            "Create linked: a differencing disk over the parent before New-VM, the parent marked read-only and reported");
        mock.ClearCalls();
        var attachedParent = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("Linked B") { Source = "linked", ParentPath = box.P(@"VHDs\existing.vhdx") }, default));
        check(attachedParent?.ErrorType == "InvalidArgument" && !mock.Called("New-VHD") && !Mutated(mock), "Create linked refuses a parent that a VM attaches");

        mock.ClearCalls();
        await lifecycle.CreateAsync(new VmCreateRequest("Tux") { OsFamily = "linux", DiskGb = 2, SwitchName = "" }, default);
        check(Single(mock, "Set-VMFirmware", p => p.TryGetProperty("SecureBootTemplate", out _)).GetProperty("SecureBootTemplate").GetString() == "MicrosoftUEFICertificateAuthority"
            && Single(mock, "Set-VM").GetProperty("EnhancedSessionTransportType").GetString() == "HvSocket" && !mock.Called("Enable-VMTPM") && !mock.Called("Set-VMKeyProtector")
            && !Single(mock, "New-VM").TryGetProperty("SwitchName", out _),
            "Create linux: the UEFI CA template and HvSocket transport, no vTPM; an empty switchName leaves the adapter disconnected");

        mock.ClearCalls();
        await lifecycle.CreateAsync(new VmCreateRequest("Lab Net") { Source = "none", SwitchName = "Lab Switch", OsFamily = "other" }, default);
        var missingSwitch = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("No Net") { Source = "none", SwitchName = "Nope" }, default));
        check(First(mock, "New-VM", p => Str(p, "SwitchName") == "Lab Switch") >= 0
            && First(mock, "Set-VMFirmware", p => Str(p, "EnableSecureBoot") == "Off") >= 0
            && missingSwitch?.ErrorType == "NotFound" && Calls(mock, "New-VM").Count == 1,
            "Create resolves an explicit switch by exact name, refuses an unknown one, and turns Secure Boot off for osFamily=other");

        mock.ClearCalls();
        var huge = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("Huge") { DiskGb = 65536 }, default));
        check(huge?.ErrorType == "InvalidArgument" && huge.Message.Contains("free") && !Mutated(mock), "Create refuses a disk larger than the volume's free space before any change");

        SetState(mock, state => state["failOn"] = new JsonArray("Enable-VMTPM"));
        mock.ClearCalls();
        var failed = await Expect(() => lifecycle.CreateAsync(new VmCreateRequest("Rollback A") { DiskGb = 2 }, default));
        check(failed?.ErrorType == "HyperVOperationFailed" && mock.Called("Remove-VM") && !File.Exists(box.P(@"VHDs\Rollback A.vhdx"))
            && !(await service.HyperV.GetInventoryAsync()).Machines.Any(m => m.Name == "Rollback A"),
            "A failed step after New-VM removes the half-created VM and deletes its new disk, then reports the original error");
        SetState(mock, state => state.Remove("failOn"));

        check(await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("Gen1 TPM") { Generation = 1, Tpm = true }, default))
            && await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("bad/name"), default))
            && await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("Rel") { Path = "relative" }, default))
            && await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("NoDisk") { Source = "none", VhdPath = loose }, default))
            && await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("Dyn") { DynamicMemory = true, MemoryMinMb = 8192 }, default))
            && await Throws<ArgumentException>(() => lifecycle.CreateAsync(new VmCreateRequest("Iso B") { Source = "iso" }, default)),
            "Create validates generation features, names, absolute paths, source parameters and memory bounds before PowerShell runs");
    }

    private static async Task RemoveAsync(Action<bool, string> check)
    {
        using var box = new Sandbox();
        var snapFolder = box.P(@"VMs\Snap");
        var snapBase = box.File(@"VMs\Snap\Virtual Hard Disks\snap.vhdx");
        var snapA = box.File(@"VMs\Snap\Virtual Hard Disks\snap_A.avhdx");
        var snapB = box.File(@"VMs\Snap\Virtual Hard Disks\snap_B.avhdx");
        string s1 = Guid.NewGuid().ToString(), s2 = Guid.NewGuid().ToString();
        var snap = Vm("Snap", "Off", ("path", snapFolder), ("disks", new[] { snapB }),
            ("snapshots", new object[] { Snapshot(s1, null, snapBase), Snapshot(s2, s1, snapA) }));
        box.File($@"VMs\Snap\Virtual Machines\{Id(snap).ToUpperInvariant()}.vmcx");
        var baseDisk = box.File(@"VHDs\base.vhdx");
        var childDisk = box.File(@"VHDs\child.vhdx");
        var shared = box.File(@"VHDs\shared.vhdx");
        var lonely = box.File(@"VHDs\lonely.vhdx");
        var lonelyChild = box.File(@"VHDs\lonely-child.vhdx");
        var savedDisk = box.File(@"VHDs\saved1.vhdx");
        var isoDisk = box.File(@"VHDs\isovm.vhdx");
        var iso = box.File(@"iso\x.iso");
        using var mock = new MockHyperV(new
        {
            host = Host(box), switches = Switches(),
            vms = new object[]
            {
                snap, Vm("Base", "Off", ("disks", new[] { baseDisk })), Vm("Child", "Off", ("disks", new[] { childDisk })),
                Vm("Shared A", "Off", ("disks", new[] { shared })), Vm("Shared B", "Off", ("disks", new[] { shared })),
                Vm("Lonely", "Off", ("disks", new[] { lonely })), Vm("Running One", "Running"), Vm("Paused One", "Paused"),
                Vm("Saved Snap", "Saved", ("snapshots", new object[] { Snapshot(Guid.NewGuid().ToString(), null) })),
                Vm("Saved One", "Saved", ("disks", new[] { savedDisk })), Vm("Iso VM", "Off", ("disks", new[] { isoDisk }), ("dvd", iso))
            },
            vhds = new object[] { Vhd(snapB, snapA), Vhd(snapA, snapBase), Vhd(childDisk, baseDisk), Vhd(lonelyChild, lonely) }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        var running = await Expect(() => lifecycle.RemoveAsync("Running One", true, false, default));
        var paused = await Expect(() => lifecycle.RemoveAsync("Paused One", false, false, default));
        var savedSnap = await Expect(() => lifecycle.RemoveAsync("Saved Snap", true, false, default));
        check(running?.ErrorType == "InvalidVmState" && paused?.ErrorType == "InvalidVmState" && savedSnap?.ErrorType == "InvalidVmState"
            && savedSnap.Message.Contains("checkpoint") && !Mutated(mock),
            "Remove never stops a VM: Running and Paused are refused, and a Saved VM with checkpoints cannot delete its disks; nothing changes");

        mock.ClearCalls();
        var plan = (await lifecycle.RemoveAsync("Snap", true, true, default)).GetProperty("plan");
        check(plan.GetProperty("checkpoints").GetInt32() == 2 && Texts(plan, "merge").ToHashSet().SetEquals([snapA, snapB])
            && Texts(plan, "delete").SequenceEqual([snapBase]) && plan.GetProperty("keep").GetArrayLength() == 0 && !Mutated(mock)
            && File.Exists(snapBase) && File.Exists(snapA) && File.Exists(snapB),
            "dryRun plans the checkpoint merge (.avhdx files) and deletion of the base disk they merge into, and changes nothing");

        var basePlan = (await lifecycle.RemoveAsync("Base", true, true, default)).GetProperty("plan");
        var childPlan = (await lifecycle.RemoveAsync("Child", true, true, default)).GetProperty("plan");
        var sharedPlan = (await lifecycle.RemoveAsync("Shared A", true, true, default)).GetProperty("plan");
        var lonelyPlan = (await lifecycle.RemoveAsync("Lonely", true, true, default)).GetProperty("plan");
        var isoPlan = (await lifecycle.RemoveAsync("Iso VM", true, true, default)).GetProperty("plan");
        var keepPlan = (await lifecycle.RemoveAsync("Snap", false, true, default)).GetProperty("plan");
        check(basePlan.GetProperty("delete").GetArrayLength() == 0 && Reason(basePlan, baseDisk).Contains("differencing parent") && Reason(basePlan, baseDisk).Contains("'Child'")
            && Texts(childPlan, "delete").SequenceEqual([childDisk]) && childPlan.GetProperty("keep").GetArrayLength() == 0
            && Reason(sharedPlan, shared).Contains("attached to VM 'Shared B'")
            && Reason(lonelyPlan, lonely).Contains("lonely-child.vhdx")
            && Reason(isoPlan, iso).Contains("never deleted") && Texts(isoPlan, "delete").SequenceEqual([isoDisk])
            && Reason(keepPlan, snapBase) == "deleteDisks is false" && keepPlan.GetProperty("delete").GetArrayLength() == 0 && !Mutated(mock),
            "Removal plans keep differencing parents, disks shared with another VM, parents of unattached disks beside them and ISOs, each with its reason");

        mock.ClearCalls();
        var removed = await lifecycle.RemoveAsync("Snap", true, false, default);
        var removeSnapshot = First(mock, "Remove-VMSnapshot", p => Str(p, "VMSnapshot") == s1 && Flag(p, "IncludeAllChildSnapshots"));
        check(removeSnapshot >= 0 && removeSnapshot < First(mock, "Remove-VM") && removed.GetProperty("checkpointsRemoved").GetInt32() == 2
            && Texts(removed, "deleted").SequenceEqual([snapBase]) && !File.Exists(snapBase) && !File.Exists(snapA) && !File.Exists(snapB)
            && Texts(removed, "removedDirectories").SequenceEqual([snapFolder]) && !Directory.Exists(snapFolder)
            && !(await service.HyperV.GetInventoryAsync()).Machines.Any(m => m.Name == "Snap"),
            "deleteDisks removes the checkpoints first and waits for the merge, then the VM, then the base disk, verified gone with the emptied VM folder");

        mock.ClearCalls();
        var childRemoved = await lifecycle.RemoveAsync("Child", true, false, default);
        var savedRemoved = await lifecycle.RemoveAsync("Saved One", false, false, default);
        check(Texts(childRemoved, "deleted").SequenceEqual([childDisk]) && !File.Exists(childDisk) && File.Exists(baseDisk)
            && savedRemoved.GetProperty("success").GetBoolean() && File.Exists(savedDisk) && Reason(savedRemoved, savedDisk, "kept") == "deleteDisks is false"
            && Calls(mock, "Remove-VM").Count == 2,
            "A linked clone's removal deletes its own differencing disk but never its parent; a Saved VM is removed with its disks kept");

        var tools = new HyperVTools(service);
        var envelope = await tools.RemoveVm("Running One");
        using (var document = JsonDocument.Parse(envelope.Content.OfType<TextContentBlock>().Single().Text))
            check(envelope.IsError == true && document.RootElement.GetProperty("errorType").GetString() == "InvalidVmState", "hyperv_vm_remove reports refusals in the uniform envelope");
    }

    private static async Task FirmwareAsync(Action<bool, string> check)
    {
        using var mock = new MockHyperV(new
        {
            switches = Switches(),
            vms = new object[]
            {
                Vm("Fw", "Off"), Vm("Fw Kp", "Off", ("keyProtector", true), ("disks", new[] { @"C:\MockVHDs\fwkp.vhdx" })), Vm("Fw Run", "Running"), Vm("Fw Gen1", "Off", ("generation", 1)),
                Vm("Fw Tpm", "Off", ("tpm", true), ("keyProtector", true))
            }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        var result = await lifecycle.FirmwareAsync("Fw", true, "MicrosoftUEFICertificateAuthority", true, null, default);
        var template = First(mock, "Set-VMFirmware", p => p.TryGetProperty("SecureBootTemplate", out _));
        var secureBoot = First(mock, "Set-VMFirmware", p => Str(p, "EnableSecureBoot") == "On");
        var protector = First(mock, "Set-VMKeyProtector");
        var tpm = First(mock, "Enable-VMTPM");
        check(template >= 0 && template < secureBoot && secureBoot < protector && protector < tpm
            && result.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && result.GetProperty("security").GetProperty("KeyProtector").GetBoolean()
            && result.GetProperty("firmware").GetProperty("SecureBootTemplate").GetString() == "MicrosoftUEFICertificateAuthority"
            && result.GetProperty("secureBootEnabled").GetBoolean() && Texts(result, "warnings").Any(w => w.Contains("key protector")),
            "Firmware order: template, Secure Boot, a new local key protector, then the vTPM; security is read back and verified");

        mock.ClearCalls();
        await lifecycle.FirmwareAsync("Fw Kp", null, null, true, "disk", default);
        check(!mock.Called("Set-VMKeyProtector") && mock.Called("Enable-VMTPM")
            && First(mock, "Set-VMFirmware", p => Str(p, "FirstBootDevice") == @"C:\MockVHDs\fwkp.vhdx") > First(mock, "Enable-VMTPM"),
            "An existing key protector is reused, and the first boot device is set last");

        mock.ClearCalls();
        var running = await Expect(() => lifecycle.FirmwareAsync("Fw Run", false, null, true, null, default));
        var gen1 = await Expect(() => lifecycle.FirmwareAsync("Fw Gen1", false, null, null, null, default));
        var noDvd = await Expect(() => lifecycle.FirmwareAsync("Fw", null, null, null, "dvd", default));
        check(running?.ErrorType == "InvalidVmState" && running.Message.Contains("secureBoot (") && running.Message.Contains("tpm (")
            && gen1?.ErrorType == "InvalidArgument" && gen1.Message.Contains("Generation 1") && noDvd?.ErrorType == "InvalidArgument" && noDvd.Message.Contains("DVD")
            && !Mutated(mock),
            "Firmware refuses Running VMs (listing every setting), Generation 1 VMs and a missing boot device, before any change");

        mock.ClearCalls();
        var read = await lifecycle.FirmwareAsync("Fw", null, null, null, null, default);
        var disabled = await lifecycle.FirmwareAsync("Fw Tpm", null, null, false, null, default);
        check(read.GetProperty("firmware").GetProperty("BootOrder").GetArrayLength() >= 1 && mock.Called("Disable-VMTPM")
            && !disabled.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && Texts(disabled, "warnings").Any(w => w.Contains("BitLocker"))
            && Calls(mock, "Set-VMFirmware").Count == 0,
            "Firmware without settings only reads; disabling the vTPM warns about sealed guest secrets");

        var tools = new HyperVTools(service);
        var legacy = await tools.Firmware("Fw", secureBoot: false);
        using (var document = JsonDocument.Parse(legacy.Content.OfType<TextContentBlock>().Single().Text))
            check(legacy.IsError != true && document.RootElement.GetProperty("success").GetBoolean() && !document.RootElement.GetProperty("secureBootEnabled").GetBoolean(),
                "The secureBoot-only hyperv_firmware call keeps working and reports secureBootEnabled");
    }

    private static async Task NetworkAsync(Action<bool, string> check)
    {
        var adapters = new object[] { new { name = "Network Adapter", switchName = "Default Switch", mac = "00155D000010", dynamicMac = true, spoofing = "Off", vlan = 0 } };
        using var mock = new MockHyperV(new
        {
            switches = Switches(), nats = new object[] { new { name = "Existing NAT", prefix = "10.0.0.0/24" } },
            vms = new object[] { Vm("Net Run2", "Running", ("adapters", adapters)), Vm("Net Run1", "Running", ("generation", 1)), Vm("Net Off", "Off", ("adapters", adapters)) }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        var added = await lifecycle.AddAdapterAsync("Net Run2", "Second", "Lab Switch", default);
        var add = Single(mock, "Add-VMNetworkAdapter");
        check(add.GetProperty("Name").GetString() == "Second" && add.GetProperty("SwitchName").GetString() == "Lab Switch" && added.GetProperty("adapters").GetArrayLength() == 2,
            "add_adapter hot-adds a named adapter to a Running Generation 2 VM");
        mock.ClearCalls();
        var gen1 = await Expect(() => lifecycle.AddAdapterAsync("Net Run1", "Second", null, default));
        var twice = await Expect(() => lifecycle.AddAdapterAsync("Net Run2", "Second", null, default));
        check(gen1?.ErrorType == "InvalidVmState" && gen1.Message.Contains("add_adapter (") && gen1.Message.Contains("Generation 2")
            && twice?.ErrorType == "InvalidArgument" && !Mutated(mock),
            "add_adapter refuses a Running Generation 1 VM and a duplicate adapter name before any change");

        mock.ClearCalls();
        var removed = await lifecycle.RemoveAdapterAsync("Net Run2", "Second", default);
        var missing = await Expect(() => lifecycle.RemoveAdapterAsync("Net Run2", "Nope", default));
        check(Single(mock, "Remove-VMNetworkAdapter").GetProperty("VMNetworkAdapter").GetString()!.EndsWith(@"\Second") && removed.GetProperty("adapters").GetArrayLength() == 1
            && missing?.ErrorType == "NotFound",
            "remove_adapter removes exactly the named adapter from a Running Generation 2 VM; an unknown name is NotFound");

        mock.ClearCalls();
        var set = await lifecycle.SetAdapterAsync("Net Run2", "Network Adapter", null, true, 10, default);
        var refusedMac = await Expect(() => lifecycle.SetAdapterAsync("Net Run2", "Network Adapter", "00155D0A0B0C", null, null, default));
        check(Single(mock, "Set-VMNetworkAdapter").GetProperty("MacAddressSpoofing").GetString() == "On"
            && Single(mock, "Set-VMNetworkAdapterVlan").GetProperty("Access").GetBoolean() && Single(mock, "Set-VMNetworkAdapterVlan").GetProperty("VlanId").GetInt32() == 10
            && set.GetProperty("adapters")[0].GetProperty("VlanId").GetInt32() == 10 && set.GetProperty("adapters")[0].GetProperty("MacAddressSpoofing").GetString() == "On"
            && refusedMac?.ErrorType == "InvalidVmState" && refusedMac.Message.Contains("staticMac ("),
            "set_adapter changes MAC spoofing and VLAN live and refuses a MAC change while Running");
        mock.ClearCalls();
        await lifecycle.SetAdapterAsync("Net Off", "Network Adapter", "00-15-5d-0a-0b-0c", null, 0, default);
        check(Single(mock, "Set-VMNetworkAdapter").GetProperty("StaticMacAddress").GetString() == "00155D0A0B0C" && Single(mock, "Set-VMNetworkAdapterVlan").GetProperty("Untagged").GetBoolean()
            && await Throws<ArgumentException>(() => lifecycle.SetAdapterAsync("Net Off", "Network Adapter", "01155D0A0B0C", null, null, default)),
            "set_adapter normalizes a static MAC on an Off VM, clears the VLAN with 0, and rejects multicast MACs");

        mock.ClearCalls();
        await lifecycle.ConnectAdapterAsync("Net Run2", "Network Adapter", "Lab Switch", default);
        var unknownSwitch = await Expect(() => lifecycle.ConnectAdapterAsync("Net Run2", "Network Adapter", "Nope", default));
        await lifecycle.ConnectAdapterAsync("Net Run2", "Network Adapter", null, default);
        check(Single(mock, "Connect-VMNetworkAdapter").GetProperty("SwitchName").GetString() == "Lab Switch" && unknownSwitch?.ErrorType == "NotFound" && mock.Called("Disconnect-VMNetworkAdapter"),
            "connect/disconnect work while Running and refuse an unknown switch");

        mock.ClearCalls();
        var nat = await lifecycle.CreateSwitchAsync("Lab NAT", "NAT", "192.168.200.0/24", default);
        var newSwitch = First(mock, "New-VMSwitch", p => Str(p, "SwitchType") == "Internal" && Str(p, "Name") == "Lab NAT");
        var ip = First(mock, "New-NetIPAddress", p => Str(p, "IPAddress") == "192.168.200.1" && Num(p, "PrefixLength") == 24 && Num(p, "InterfaceIndex") == 40);
        var winNat = First(mock, "New-NetNat", p => Str(p, "InternalIPInterfaceAddressPrefix") == "192.168.200.0/24" && Str(p, "Name") == "Lab NAT");
        check(newSwitch >= 0 && newSwitch < ip && ip < winNat && nat.GetProperty("nat").GetProperty("gateway").GetString() == "192.168.200.1",
            "create NAT: an Internal switch, the gateway address on its host adapter, then the WinNAT network");
        mock.ClearCalls();
        var overlap = await Expect(() => lifecycle.CreateSwitchAsync("Overlap", "NAT", "10.0.0.0/16", default));
        check(overlap?.ErrorType == "InvalidArgument" && overlap.Message.Contains("Existing NAT") && !Mutated(mock)
            && await Throws<ArgumentException>(() => lifecycle.CreateSwitchAsync("Bad", "NAT", "192.168.200.5/24", default))
            && await Throws<ArgumentException>(() => lifecycle.CreateSwitchAsync("Ext", "External", null, default)),
            "create NAT refuses an overlapping NAT prefix before any change, an unaligned subnet, and External switches");
        mock.ClearCalls();
        var removedSwitch = await lifecycle.RemoveSwitchAsync("Lab NAT", default);
        check(First(mock, "Remove-VMSwitch") < First(mock, "Remove-NetNat") && Texts(removedSwitch, "removedNat").SequenceEqual(["Lab NAT"]),
            "remove deletes the switch and then its NAT network");
    }

    private static async Task MediaAsync(Action<bool, string> check)
    {
        using var box = new Sandbox();
        var boot = box.File(@"VHDs\boot.vhdx");
        var data = box.File(@"VHDs\data.vhdx");
        var extra = box.File(@"VHDs\extra.vhdx");
        var extra2 = box.File(@"VHDs\extra2.vhdx");
        var ide = box.File(@"VHDs\ide.vhdx");
        var iso = box.File(@"iso\x.iso");
        var running = Vm("Disk Run", "Running", ("disks", new[] { boot, data }), ("dvd", ""));
        var saved = Vm("Disk Saved", "Saved");
        using var mock = new MockHyperV(new
        {
            host = Host(box), switches = Switches(),
            vms = new object[]
            {
                running, saved,
                Vm("Disk Run1", "Running", ("generation", 1), ("disks", new object[] { new { path = ide, controllerType = "IDE", controllerNumber = 0, controllerLocation = 0 } }))
            }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        await lifecycle.DiskAsync("Disk Run", "attach_vhd", extra, null, default);
        await lifecycle.DiskAsync("Disk Run1", "attach_vhd", extra2, null, default);
        await lifecycle.DiskAsync("Disk Run", "detach_vhd", data, null, default);
        await lifecycle.DiskAsync("Disk Run", "attach_iso", iso, null, default);
        await lifecycle.DiskAsync("Disk Run", "eject_iso", null, null, default);
        var resized = await lifecycle.DiskAsync("Disk Run", "resize_vhd", boot, 80, default);
        check(Calls(mock, "Add-VMHardDiskDrive").All(c => c.GetProperty("ControllerType").GetString() == "SCSI") && Calls(mock, "Add-VMHardDiskDrive").Count == 2
            && Single(mock, "Remove-VMHardDiskDrive").GetProperty("VMHardDiskDrive").GetString() == data
            && Calls(mock, "Set-VMDvdDrive").Count == 2 && Calls(mock, "Set-VMDvdDrive")[0].GetProperty("Path").GetString() == iso
            && Single(mock, "Resize-VHD").GetProperty("SizeBytes").GetInt64() == 80L << 30 && resized.GetProperty("success").GetBoolean(),
            "Running: SCSI disk attach (Generation 1 too) and detach, ISO insert and eject, and online VHDX resize on SCSI");

        mock.ClearCalls();
        var bootDetach = await Expect(() => lifecycle.DiskAsync("Disk Run", "detach_vhd", boot, null, default));
        var ideDetach = await Expect(() => lifecycle.DiskAsync("Disk Run1", "detach_vhd", ide, null, default));
        var shared = await Expect(() => lifecycle.DiskAsync("Disk Run1", "attach_vhd", boot, null, default));
        check(bootDetach?.ErrorType == "InvalidVmState" && bootDetach.Message.Contains("boot disk") && ideDetach?.ErrorType == "InvalidVmState"
            && shared?.ErrorType == "InvalidArgument" && shared.Message.Contains("attached to VM 'Disk Run'") && !Mutated(mock),
            "Running: the boot disk and IDE disks stay attached, and a disk another VM uses is never attached; nothing changes");

        mock.ClearCalls();
        var com = await lifecycle.SetComPortAsync(Guid.Parse(Id(running)), 2, @"\\.\pipe\HyperVControl-test", default);
        var savedCom = await Expect(() => lifecycle.SetComPortAsync(Guid.Parse(Id(saved)), 2, @"\\.\pipe\HyperVControl-test", default));
        check(Single(mock, "Set-VMComPort").GetProperty("Path").GetString() == @"\\.\pipe\HyperVControl-test" && Single(mock, "Set-VMComPort").GetProperty("Number").GetInt32() == 2
            && com.GetProperty("Note").GetString()!.Contains("turned off and started again") && savedCom?.ErrorType == "InvalidVmState",
            "COM port pipes map while Running (with the Generation 2 cold-boot note) and are refused for a Saved VM");
    }

    private static async Task VhdAsync(Action<bool, string> check)
    {
        using var box = new Sandbox();
        var attached = box.File(@"disks\attached.vhdx");
        var template = box.File(@"disks\tmpl.vhdx");
        var kid = box.File(@"disks\kid.vhdx");
        var loose = box.File(@"disks\loose.vhdx");
        var orphanParent = box.File(@"disks\orphan-parent.vhdx");
        var orphanChild = box.File(@"disks\orphan-child.vhdx");
        using var mock = new MockHyperV(new
        {
            host = Host(box), switches = Switches(),
            vms = new object[] { Vm("Vhd User", "Off", ("disks", new[] { attached })), Vm("Vhd Kid", "Off", ("disks", new[] { kid })) },
            vhds = new object[] { Vhd(kid, template), Vhd(orphanChild, orphanParent), Vhd(loose, size: 5L << 30) }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        mock.ClearCalls();
        var data = box.P(@"new\data.vhdx");
        var created = await lifecycle.VhdAsync("create", data, "dynamic", 2, null, false, default);
        await lifecycle.VhdAsync("create", box.P(@"new\fixed.vhdx"), "fixed", 1, null, false, default);
        var exists = await Expect(() => lifecycle.VhdAsync("create", loose, "dynamic", 2, null, false, default));
        check(First(mock, "New-VHD", p => Str(p, "Path") == data && Num(p, "SizeBytes") == 2L << 30 && Flag(p, "Dynamic")) >= 0
            && First(mock, "New-VHD", p => Flag(p, "Fixed")) >= 0 && File.Exists(data)
            && Texts(created, "createdFiles").SequenceEqual([data]) && created.GetProperty("type").GetString() == "Dynamic"
            && exists?.ErrorType == "InvalidArgument" && Calls(mock, "New-VHD").Count == 2,
            "vhd create makes dynamic and fixed disks in a new folder and never overwrites an existing file");

        mock.ClearCalls();
        var diff = box.P(@"new\diff.vhdx");
        var differencing = await lifecycle.VhdAsync("create", diff, "differencing", null, loose, false, default);
        var overAttached = await Expect(() => lifecycle.VhdAsync("create", box.P(@"new\bad.vhdx"), "differencing", null, attached, false, default));
        check(Single(mock, "New-VHD").GetProperty("ParentPath").GetString() == loose && differencing.GetProperty("chain").GetArrayLength() == 2
            && File.GetAttributes(loose).HasFlag(FileAttributes.ReadOnly) && overAttached?.ErrorType == "InvalidArgument" && Calls(mock, "New-VHD").Count == 1,
            "vhd create differencing marks its parent read-only and refuses a parent that a VM attaches");

        var kidInfo = await lifecycle.VhdAsync("inspect", kid, "dynamic", null, null, false, default);
        var templateInfo = await lifecycle.VhdAsync("inspect", template, "dynamic", null, null, false, default);
        check(kidInfo.GetProperty("chain").EnumerateArray().Select(link => link.GetProperty("Path").GetString()).SequenceEqual([kid, template])
            && kidInfo.GetProperty("attachedTo")[0].GetProperty("vmName").GetString() == "Vhd Kid"
            && templateInfo.GetProperty("attachedTo").GetArrayLength() == 0 && templateInfo.GetProperty("parentOf")[0].GetProperty("vmName").GetString() == "Vhd Kid",
            "vhd inspect reports the parent chain, attaching VMs and the disks a file is a parent of");

        mock.ClearCalls();
        var attachedDelete = await Expect(() => lifecycle.VhdAsync("delete", attached, "dynamic", null, null, false, default));
        var templatePlan = await lifecycle.VhdAsync("delete", template, "dynamic", null, null, true, default);
        var orphanPlan = await lifecycle.VhdAsync("delete", orphanParent, "dynamic", null, null, true, default);
        var dataPlan = await lifecycle.VhdAsync("delete", data, "dynamic", null, null, true, default);
        check(attachedDelete?.ErrorType == "InvalidArgument" && attachedDelete.Message.Contains("attached to VM 'Vhd User'") && File.Exists(attached)
            && !templatePlan.GetProperty("deletable").GetBoolean() && Texts(templatePlan, "blockers").Any(b => b.Contains("differencing parent"))
            && !orphanPlan.GetProperty("deletable").GetBoolean() && Texts(orphanPlan, "blockers").Any(b => b.Contains("orphan-child.vhdx"))
            && dataPlan.GetProperty("deletable").GetBoolean() && File.Exists(data) && !Mutated(mock),
            "vhd delete refuses attached disks and differencing parents (also of unattached disks); dryRun changes nothing");
        var deleted = await lifecycle.VhdAsync("delete", data, "dynamic", null, null, false, default);
        check(Texts(deleted, "deleted").SequenceEqual([data]) && !File.Exists(data)
            && await Throws<ArgumentException>(() => lifecycle.VhdAsync("delete", box.File(@"iso\a.iso"), "dynamic", null, null, false, default))
            && await Throws<ArgumentException>(() => lifecycle.VhdAsync("create", box.P("x.vhdx"), "dynamic", null, null, false, default)),
            "vhd delete removes an unused disk; non-disk files and a missing sizeGb fail validation");
    }

    private static async Task CloneAndInfoAsync(Action<bool, string> check)
    {
        using var box = new Sandbox();
        var source = box.File(@"src\src.vhdx");
        var snapDisk = box.File(@"src\snap.vhdx");
        var adapters = new object[] { new { name = "Network Adapter", switchName = "Default Switch", mac = "00155D0000AA", dynamicMac = false, spoofing = "Off", vlan = 0 } };
        using var mock = new MockHyperV(new
        {
            host = Host(box), switches = Switches(),
            vms = new object[]
            {
                Vm("Src", "Off", ("disks", new[] { source }), ("adapters", adapters), ("tpm", true), ("keyProtector", true), ("template", "MicrosoftUEFICertificateAuthority"),
                    ("dynamicMemory", true), ("memoryMinimum", 1L << 30), ("memoryMaximum", 8L << 30), ("processorCount", 4), ("nested", true), ("notes", "golden")),
                Vm("Src Run", "Running", ("disks", new[] { box.File(@"src\run.vhdx") })),
                Vm("Src Snap", "Off", ("disks", new[] { snapDisk }), ("snapshots", new object[] { Snapshot(Guid.NewGuid().ToString(), null, snapDisk) }))
            }
        });
        using var service = new ControlService();
        var lifecycle = service.Lifecycle;

        var info = await lifecycle.InfoAsync("Src", default);
        check(info.GetProperty("state").GetString() == "Off" && info.GetProperty("configurationVersion").GetString() == "12.0" && info.GetProperty("creationTime").GetString()!.StartsWith("2026-01-02")
            && info.GetProperty("notes").GetString() == "golden" && info.GetProperty("memory").GetProperty("maximumBytes").GetInt64() == 8L << 30
            && info.GetProperty("memory").GetProperty("bufferPercent").GetInt32() == 20 && info.GetProperty("processor").GetProperty("nestedVirtualization").GetBoolean()
            && info.GetProperty("automaticActions").GetProperty("stopAction").GetString() == "Save" && info.GetProperty("checkpointCount").GetInt32() == 0
            && info.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && info.GetProperty("firmware").GetProperty("BootOrder")[0].GetProperty("Device").GetString() == source
            && info.GetProperty("disks")[0].GetProperty("Chain")[0].GetProperty("Path").GetString() == source && info.GetProperty("disks")[0].GetProperty("ControllerType").GetString() == "SCSI"
            && info.GetProperty("vm").GetProperty("Name").GetString() == "Src",
            "vm_info adds state, version, creation time, notes, memory bounds, processor, automatic actions, checkpoints, security, boot order and disk chains");
        var defaults = await lifecycle.HostDefaultsAsync(default);
        check(defaults.GetProperty("virtualMachinePath").GetString() == box.P("VMs") && defaults.GetProperty("defaultSwitch").GetProperty("present").GetBoolean()
            && defaults.GetProperty("configurationVersions").GetProperty("default").GetString() == "12.0" && defaults.GetProperty("configurationVersions").GetProperty("supported").GetArrayLength() == 5
            && defaults.GetProperty("freeSpace").GetArrayLength() == 2 && defaults.GetProperty("secureBootTemplates").GetArrayLength() == 3,
            "Capabilities host defaults: default paths, configuration versions, Secure Boot templates, Default Switch presence and free space");

        mock.ClearCalls();
        var running = await Expect(() => lifecycle.CloneAsync("Src Run", "Clone X", "linked", null, default));
        var withCheckpoints = await Expect(() => lifecycle.CloneAsync("Src Snap", "Clone Y", "linked", null, default));
        var taken = await Expect(() => lifecycle.CloneAsync("Src", "Src Run", "linked", null, default));
        check(running?.ErrorType == "InvalidVmState" && withCheckpoints?.ErrorType == "InvalidVmState" && withCheckpoints.Message.Contains("checkpoints")
            && taken?.ErrorType == "InvalidArgument" && !Mutated(mock)
            && await Throws<ArgumentException>(() => lifecycle.CloneAsync("Src", "Clone Z", "copy", null, default)),
            "Clone refuses a Running source, a source with checkpoints and an existing name before any change");

        mock.ClearCalls();
        var linked = await lifecycle.CloneAsync("Src", "Src Clone", "linked", box.P("clones"), default);
        var child = box.P(@"clones\Src Clone\Virtual Hard Disks\Src Clone.vhdx");
        var newVhd = First(mock, "New-VHD", p => Str(p, "Path") == child && Str(p, "ParentPath") == source && Flag(p, "Differencing"));
        var newVm = First(mock, "New-VM", p => Str(p, "VHDPath") == child && Str(p, "Path") == box.P("clones") && Num(p, "Generation") == 2 && Num(p, "MemoryStartupBytes") == 4L << 30);
        var cloneMemory = Single(mock, "Set-VMMemory");
        var template = First(mock, "Set-VMFirmware", p => Str(p, "SecureBootTemplate") == "MicrosoftUEFICertificateAuthority");
        check(newVhd >= 0 && newVhd < newVm && Single(mock, "Set-VMProcessor").GetProperty("Count").GetInt64() == 4 && Single(mock, "Set-VMProcessor").GetProperty("ExposeVirtualizationExtensions").GetBoolean()
            && cloneMemory.GetProperty("DynamicMemoryEnabled").GetBoolean() && cloneMemory.GetProperty("MaximumBytes").GetInt64() == 8L << 30
            && template > newVm && template < First(mock, "Set-VMKeyProtector") && First(mock, "Set-VMKeyProtector") < First(mock, "Enable-VMTPM")
            && First(mock, "Remove-VMNetworkAdapter") < First(mock, "Add-VMNetworkAdapter", p => Str(p, "SwitchName") == "Default Switch" && !p.TryGetProperty("StaticMacAddress", out _))
            && File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly) && Texts(linked, "readOnlyParents").SequenceEqual([source])
            && linked.GetProperty("mode").GetString() == "linked" && linked.GetProperty("State").GetString() == "Off" && Texts(linked, "warnings").Any(w => w.Contains("read-only")),
            "Linked clone: differencing disks over the source disks, the source's generation, memory, vCPUs, template and a new vTPM, adapters with new MACs; parents marked read-only");

        mock.ClearCalls();
        var full = await lifecycle.CloneAsync("Src Snap", "Src Full", "full", box.P("full"), default);
        var export = First(mock, "Export-VM", p => Str(p, "Path")?.StartsWith(box.P(@"full\.hyperv-control-export-")) == true);
        var import = First(mock, "Import-VM", p => Flag(p, "Copy") && Flag(p, "GenerateNewId")
            && Str(p, "VirtualMachinePath") == box.P(@"full\Src Full") && Str(p, "VhdDestinationPath") == box.P(@"full\Src Full\Virtual Hard Disks")
            && Str(p, "SnapshotFilePath") == box.P(@"full\Src Full") && Str(p, "SmartPagingFilePath") == box.P(@"full\Src Full"));
        check(export >= 0 && export < import && import < First(mock, "Rename-VM", p => Str(p, "NewName") == "Src Full") && mock.Called("Set-VHD")
            && !Directory.EnumerateDirectories(box.P("full"), ".hyperv-control-export-*").Any() && full.GetProperty("mode").GetString() == "full" && full.GetProperty("Name").GetString() == "Src Full"
            && Texts(full, "createdFiles").SequenceEqual([box.P(@"full\Src Full\Virtual Hard Disks\snap.vhdx")]),
            "Full clone: Export-VM into a temporary folder, Import-VM -Copy -GenerateNewId into fresh folders, rename, reset disk IDs, temporary export removed");
    }

    /// <summary>A temporary folder for mock disk files; read-only attributes are cleared before deletion.</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HyperVControl-v2-" + Guid.NewGuid().ToString("N")[..8]);
        public Sandbox() => Directory.CreateDirectory(Root);
        public string P(string relative) => Path.Combine(Root, relative);

        public string File(string relative)
        {
            var path = P(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, object?> Vm(string name, string state, params (string Key, object? Value)[] extra)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(), ["name"] = name, ["state"] = state, ["generation"] = 2, ["processorCount"] = 2,
            ["memoryStartup"] = 4L << 30, ["dynamicMemory"] = false, ["disks"] = Array.Empty<object>(), ["snapshots"] = Array.Empty<object>()
        };
        foreach (var (key, value) in extra) row[key] = value;
        return row;
    }

    private static string Id(Dictionary<string, object?> vm) => (string)vm["id"]!;
    private static object Host(Sandbox box) => new { virtualMachinePath = box.P("VMs"), virtualHardDiskPath = box.P("VHDs") };
    private static object Vhd(string path, string? parent = null, long size = 64L << 30) => new { path, parentPath = parent, size, type = parent is null ? "Dynamic" : "Differencing" };
    private static object Snapshot(string id, string? parent, params string[] disks) => new { id, name = "checkpoint " + id[..4], creationTime = "2026-01-01T00:00:00Z", parentId = parent, disks };
    private static object[] Switches() =>
        [new { id = DefaultSwitchId, name = "Default Switch", type = "Internal" }, new { id = Guid.NewGuid().ToString(), name = "Lab Switch", type = "Private" }];

    private static void SetState(MockHyperV mock, Action<JsonObject> change)
    {
        var state = JsonNode.Parse(File.ReadAllText(mock.StatePath))!.AsObject();
        change(state);
        File.WriteAllText(mock.StatePath, state.ToJsonString());
    }

    private static bool Mutated(MockHyperV mock) => mock.Calls().Any(call => MutatingVerbs.Contains(call.Cmdlet.Split('-')[0]));
    private static List<JsonElement> Calls(MockHyperV mock, string cmdlet) => mock.Calls().Where(c => c.Cmdlet == cmdlet).Select(c => c.Parameters).ToList();
    private static JsonElement Single(MockHyperV mock, string cmdlet, Func<JsonElement, bool>? where = null) =>
        Calls(mock, cmdlet).Where(p => where?.Invoke(p) ?? true).ToList() is [var only] ? only : throw new Exception($"Expected exactly one {cmdlet} call.");
    private static int First(MockHyperV mock, string cmdlet, Func<JsonElement, bool>? where = null) =>
        mock.Calls().ToList().FindIndex(c => c.Cmdlet == cmdlet && (where?.Invoke(c.Parameters) ?? true));
    private static IEnumerable<string> Texts(JsonElement element, string property) => element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!);
    private static string? Str(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static long Num(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.MinValue;

    private static string Reason(JsonElement plan, string path, string list = "keep") =>
        plan.GetProperty(list).EnumerateArray().FirstOrDefault(e => e.GetProperty("path").GetString() == path) is { ValueKind: JsonValueKind.Object } entry
            ? entry.GetProperty("reason").GetString()! : "";

    private static async Task<HyperVOperationException?> Expect(Func<Task> action)
    {
        try { await action(); return null; }
        catch (HyperVOperationException ex) { return ex; }
    }

    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }
}
