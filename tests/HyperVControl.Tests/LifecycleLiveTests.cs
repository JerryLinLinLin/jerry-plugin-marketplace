using System.Text.Json;

namespace HyperVControl.Tests;

/// <summary>
/// Live validation of lifecycle v2 on the local Hyper-V host (phase <c>lifecycle-live</c>), authorized for
/// throwaway VMs only. Every VM is named <see cref="Prefix"/>&lt;run&gt;-&lt;part&gt;, every file stays under
/// <see cref="Root"/>\&lt;run&gt;, adapters stay disconnected, and other VMs and switches are only read
/// (before and after, to prove they did not change). Everything the run created is removed in a finally
/// block. Each step is recorded in lifecycle-live-summary.json beside the per-call outputs.
/// </summary>
internal static class LifecycleLiveTests
{
    internal const string Root = @"D:\HyperVControl-validation\lifecycle";
    internal const string Prefix = "HyperVControl-validation-lifecycle-";

    private sealed record Step(string Name, bool Passed, string Detail);

    internal static async Task RunAsync(ToolCall call, Action<bool, string> check, string results)
    {
        var run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var folder = Path.Combine(Root, run);
        // The harness creates the folder (not the elevated server) so its files stay deletable by this user.
        Directory.CreateDirectory(folder);
        string Name(string part) => Prefix + run + "-" + part;
        var (a, b, c, d) = (Name("a"), Name("b"), Name("clone"), Name("checkpoints"));
        var (e, f, g, h) = (Name("iso"), Name("existing"), Name("linked"), Name("full"));
        var steps = new List<Step>();
        var leftovers = new List<string>();
        var baselineVms = Machines(await call("hyperv_list", label: "lifecycle-live-baseline-vms"));
        var baselineSwitches = (await call("hyperv_network", new { action = "list" }, label: "lifecycle-live-baseline-switches")).GetRawText();
        check(!baselineVms.Any(m => m.Name.StartsWith(Prefix + run, StringComparison.Ordinal)), "No VM of this run exists before it starts");

        async Task Run(string name, Func<Task<string>> body)
        {
            try { var detail = await body(); steps.Add(new(name, true, detail)); Console.WriteLine("PASS " + name + (detail.Length > 0 ? " - " + detail : "")); }
            catch (Exception ex) { steps.Add(new(name, false, ex.Message)); Console.WriteLine("FAIL " + name + " - " + ex.Message); }
        }
        static void Expect(bool condition, string what) { if (!condition) throw new Exception(what); }
        async Task<JsonElement> Tool(string label, string name, object arguments, bool allowError = false) => await call(name, arguments, allowError, "lifecycle-live-" + label);

        try
        {
            await Run("capabilities report host defaults", async () =>
            {
                var caps = await Tool("capabilities", "hyperv_capabilities", new { });
                var defaults = caps.GetProperty("hostDefaults");
                Expect(defaults.TryGetProperty("virtualMachinePath", out _) && defaults.GetProperty("configurationVersions").GetProperty("supported").GetArrayLength() > 0
                    && defaults.GetProperty("freeSpace").GetArrayLength() > 0, "hostDefaults incomplete: " + defaults.GetRawText());
                return "default version " + defaults.GetProperty("configurationVersions").GetProperty("default").GetString()
                    + ", Default Switch present: " + defaults.GetProperty("defaultSwitch").GetProperty("present").GetBoolean();
            });

            string? diskA = null;
            await Run("create new_disk (Windows defaults, 1 GB disk, disconnected)", async () =>
            {
                var vm = await Tool("create-a", "hyperv_vm_create", new { name = a, path = folder, source = "new_disk", diskGb = 1, memoryMb = 1024, switchName = "" });
                diskA = vm.GetProperty("createdFiles")[0].GetString();
                Expect(vm.GetProperty("State").GetString() == "Off" && vm.GetProperty("Generation").GetInt32() == 2 && vm.GetProperty("ProcessorCount").GetInt32() == 2
                    && vm.GetProperty("MemoryStartup").GetInt64() == 1L << 30 && !vm.GetProperty("DynamicMemoryEnabled").GetBoolean()
                    && vm.GetProperty("CheckpointType").GetString() == "Standard" && !vm.GetProperty("AutomaticCheckpointsEnabled").GetBoolean()
                    && vm.GetProperty("Firmware").GetProperty("SecureBoot").GetString() == "On" && vm.GetProperty("Firmware").GetProperty("SecureBootTemplate").GetString() == "MicrosoftWindows"
                    && vm.GetProperty("Security").GetProperty("TpmEnabled").GetBoolean() && vm.GetProperty("Security").GetProperty("KeyProtector").GetBoolean() == true
                    && vm.GetProperty("Firmware").GetProperty("BootOrder")[0].GetProperty("BootType").GetString() == "Drive"
                    && diskA!.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(diskA), "unexpected create result: " + vm.GetRawText());
                return diskA!;
            });

            await Run("duplicate name refused before any change", async () =>
            {
                var before = Machines(await call("hyperv_list")).Count;
                var refused = await Tool("create-duplicate", "hyperv_vm_create", new { name = a, path = folder, source = "none" }, true);
                Expect(Failure(refused) == "InvalidArgument" && Machines(await call("hyperv_list")).Count == before, refused.GetRawText());
                return refused.GetProperty("error").GetString()!;
            });

            await Run("configure while Off", async () =>
            {
                var vm = await Tool("configure-off", "hyperv_vm_configure", new
                {
                    machine = a, processors = 4, notes = "lifecycle-live", autoStartAction = "Nothing", autoStopAction = "ShutDown", autoCheckpoints = false,
                    integrationServices = new Dictionary<string, bool> { ["Guest Service Interface"] = true }
                });
                Expect(vm.GetProperty("ProcessorCount").GetInt32() == 4 && vm.GetProperty("Notes").GetString() == "lifecycle-live"
                    && vm.GetProperty("AutomaticStartAction").GetString() == "Nothing" && vm.GetProperty("AutomaticStopAction").GetString() == "ShutDown"
                    && vm.GetProperty("IntegrationServices").GetProperty("Guest Service Interface").GetBoolean(), vm.GetRawText());
                return "4 vCPUs, notes, start/stop actions, Guest Service Interface on";
            });

            await Run("nested virtualization toggles while Off", async () =>
            {
                var on = await Tool("configure-nested-on", "hyperv_vm_configure", new { machine = a, nestedVirtualization = true }, true);
                if (Failure(on) is { } error) return "host refused (" + error + "): " + on.GetProperty("error").GetString();
                var off = await Tool("configure-nested-off", "hyperv_vm_configure", new { machine = a, nestedVirtualization = false });
                Expect(on.GetProperty("NestedVirtualization").GetBoolean() && !off.GetProperty("NestedVirtualization").GetBoolean(), on.GetRawText());
                return "on then off";
            });

            await Run("firmware read shows the vTPM and its key protector", async () =>
            {
                var firmware = await Tool("firmware-read", "hyperv_firmware", new { machine = a });
                Expect(firmware.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && firmware.GetProperty("security").GetProperty("KeyProtector").GetBoolean()
                    && firmware.GetProperty("firmware").GetProperty("BootOrder").GetArrayLength() > 0, firmware.GetRawText());
                return firmware.GetProperty("firmware").GetProperty("BootOrder").GetArrayLength() + " boot entries";
            });

            await Run("start without an OS (stays at the firmware)", async () =>
            {
                var started = await Tool("start-a", "hyperv_power", new { machine = a, action = "start" });
                Expect(started.GetProperty("machine").GetProperty("state").GetString() == "running", started.GetRawText());
                return "running";
            });

            // Runtime resize of static memory needs a booted guest OS that supports it; this VM has none, so
            // Hyper-V itself refuses. The step checks that the state gate passes the request on and that the
            // outcome is either the resize or that refusal with memory unchanged, and records which.
            await Run("static memory resize while Running passes the gate (succeeds only with a booted guest)", async () =>
            {
                var resized = await Tool("configure-running-memory", "hyperv_vm_configure", new { machine = a, memoryMb = 2048 }, true);
                var info = await Tool("info-after-resize", "hyperv_vm_info", new { machine = a });
                var startup = info.GetProperty("memory").GetProperty("startupBytes").GetInt64();
                if (Failure(resized) is null)
                {
                    Expect(startup == 2L << 30, "resize reported success but startup memory is " + (startup >> 20) + " MB");
                    return "1024 -> 2048 MB";
                }
                Expect(Failure(resized) == "HyperVOperationFailed" && resized.GetProperty("errorId").GetString()!.StartsWith("OperationFailed,") && startup == 1L << 30,
                    "unexpected outcome: " + resized.GetRawText() + "; startup now " + (startup >> 20) + " MB");
                return "NOT RESIZED: Hyper-V refused without a booted guest OS (" + resized.GetProperty("errorId").GetString() + "); memory unchanged at 1024 MB";
            });

            await Run("vCPU change while Running refused with the settings that need Off", async () =>
            {
                var refused = await Tool("configure-running-cpu", "hyperv_vm_configure", new { machine = a, processors = 2, notes = "must not apply" }, true);
                var info = await Tool("info-after-cpu-refusal", "hyperv_vm_info", new { machine = a });
                var message = refused.GetProperty("error").GetString()!;
                Expect(Failure(refused) == "InvalidVmState" && message.Contains("processors (vCPU count") && !message.Contains("notes")
                    && message.Contains("Nothing was changed") && info.GetProperty("processor").GetProperty("count").GetInt32() == 4
                    && info.GetProperty("notes").GetString() == "lifecycle-live", refused.GetRawText());
                return message;
            });

            await Run("live configure: notes, start action, integration services", async () =>
            {
                var vm = await Tool("configure-running-live", "hyperv_vm_configure", new
                {
                    machine = a, notes = "running", autoStartAction = "StartIfRunning", integrationServices = new Dictionary<string, bool> { ["Guest Service Interface"] = false }
                });
                Expect(vm.GetProperty("Notes").GetString() == "running" && vm.GetProperty("AutomaticStartAction").GetString() == "StartIfRunning"
                    && !vm.GetProperty("IntegrationServices").GetProperty("Guest Service Interface").GetBoolean(), vm.GetRawText());
                return "applied while Running";
            });

            await Run("adapter add, set and remove while Running", async () =>
            {
                var added = await Tool("adapter-add", "hyperv_network", new { action = "add_adapter", machine = a, adapterName = "lifecycle-live-nic" });
                Expect(added.GetProperty("adapters").GetArrayLength() == 2, added.GetRawText());
                var set = await Tool("adapter-set", "hyperv_network", new { action = "set_adapter", machine = a, adapterName = "lifecycle-live-nic", macSpoofing = true, vlanId = 12 });
                var nic = set.GetProperty("adapters").EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "lifecycle-live-nic");
                Expect(nic.GetProperty("MacAddressSpoofing").GetString() == "On" && nic.GetProperty("VlanId").GetInt32() == 12, set.GetRawText());
                var mac = await Tool("adapter-mac-running", "hyperv_network", new { action = "set_adapter", machine = a, adapterName = "lifecycle-live-nic", staticMac = "00155DFE0001" }, true);
                Expect(Failure(mac) == "InvalidVmState" && mac.GetProperty("error").GetString()!.Contains("staticMac ("), mac.GetRawText());
                var removed = await Tool("adapter-remove", "hyperv_network", new { action = "remove_adapter", machine = a, adapterName = "lifecycle-live-nic" });
                Expect(removed.GetProperty("adapters").GetArrayLength() == 1, removed.GetRawText());
                return "hot add, MAC spoofing + VLAN 12, static MAC refused, hot remove";
            });

            var data = Path.Combine(folder, "data.vhdx");
            await Run("vhd create and SCSI hot attach, online resize, detach while Running", async () =>
            {
                var created = await Tool("vhd-create", "hyperv_vhd", new { action = "create", path = data, sizeGb = 1 });
                Expect(created.GetProperty("type").GetString() == "Dynamic" && File.Exists(data), created.GetRawText());
                await Tool("disk-attach-running", "hyperv_disk", new { machine = a, action = "attach_vhd", path = data });
                var resized = await Tool("disk-resize-running", "hyperv_disk", new { machine = a, action = "resize_vhd", path = data, sizeGb = 2 }, true);
                var bootDetach = await Tool("disk-detach-boot-running", "hyperv_disk", new { machine = a, action = "detach_vhd", path = diskA }, true);
                await Tool("disk-detach-running", "hyperv_disk", new { machine = a, action = "detach_vhd", path = data });
                var inspect = await Tool("vhd-inspect-data", "hyperv_vhd", new { action = "inspect", path = data });
                Expect(Failure(bootDetach) == "InvalidVmState" && inspect.GetProperty("attachedTo").GetArrayLength() == 0, bootDetach.GetRawText());
                return "resize: " + (Failure(resized) is { } error ? error + " " + resized.GetProperty("error").GetString() : (inspect.GetProperty("sizeBytes").GetInt64() >> 30) + " GB")
                    + "; boot disk detach refused";
            });

            await Run("COM port pipe while Running (serial_host)", async () =>
            {
                var com = await Tool("com-running", "hyperv_kernel_configure", new { machine = a, action = "serial_host", comPort = 2, pipeName = "HyperVControl-validation-lifecycle" }, true);
                Expect(Failure(com) is null && com.GetProperty("result").GetProperty("Path").GetString() == @"\\.\pipe\HyperVControl-validation-lifecycle"
                    && com.GetProperty("result").GetProperty("Note").GetString()!.Contains("turned off and started again"), com.GetRawText());
                return "COM 2 mapped while Running, with the Generation 2 cold-boot note";
            });

            await Run("firmware refused while Running", async () =>
            {
                var refused = await Tool("firmware-running", "hyperv_firmware", new { machine = a, secureBoot = false }, true);
                Expect(Failure(refused) == "InvalidVmState" && refused.GetProperty("error").GetString()!.Contains("secureBoot ("), refused.GetRawText());
                return "refused";
            });

            await Run("turn off", async () =>
            {
                var off = await Tool("turn-off-a", "hyperv_power", new { machine = a, action = "turn_off" });
                Expect(off.GetProperty("machine").GetProperty("state").GetString() == "off", off.GetRawText());
                return "off";
            });

            await Run("Secure Boot template locked after the vTPM booted", async () =>
            {
                var template = await Tool("firmware-template-after-boot", "hyperv_firmware", new { machine = a, secureBootTemplate = "MicrosoftUEFICertificateAuthority" }, true);
                var read = await Tool("firmware-template-readback", "hyperv_firmware", new { machine = a });
                var now = read.GetProperty("firmware").GetProperty("SecureBootTemplate").GetString();
                return Failure(template) is { } error
                    ? "refused as " + error + " (" + template.GetProperty("errorId").GetString() + "): " + template.GetProperty("error").GetString() + "; template still " + now
                    : "accepted; template now " + now;
            });

            await Run("firmware Off: Secure Boot off/on, first boot device, vTPM off/on", async () =>
            {
                var off = await Tool("firmware-secureboot-off", "hyperv_firmware", new { machine = a, secureBoot = false });
                var on = await Tool("firmware-secureboot-on", "hyperv_firmware", new { machine = a, secureBoot = true, firstBootDevice = "disk" });
                var tpmOff = await Tool("firmware-tpm-off", "hyperv_firmware", new { machine = a, tpm = false });
                var tpmOn = await Tool("firmware-tpm-on", "hyperv_firmware", new { machine = a, tpm = true });
                Expect(!off.GetProperty("secureBootEnabled").GetBoolean() && on.GetProperty("secureBootEnabled").GetBoolean()
                    && on.GetProperty("firmware").GetProperty("BootOrder")[0].GetProperty("BootType").GetString() == "Drive"
                    && !tpmOff.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && tpmOn.GetProperty("security").GetProperty("TpmEnabled").GetBoolean()
                    && !tpmOn.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("Created a local key protector")), on.GetRawText() + tpmOn.GetRawText());
                return "verified by readback; the existing key protector was reused";
            });

            await Run("vTPM recipe on a VM without key protector: template, protector, TPM", async () =>
            {
                var vm = await Tool("create-b", "hyperv_vm_create", new { name = b, path = folder, source = "none", tpm = false, memoryMb = 512, switchName = "" });
                Expect(!vm.GetProperty("Security").GetProperty("TpmEnabled").GetBoolean() && vm.GetProperty("Security").GetProperty("KeyProtector").GetBoolean() == false, vm.GetRawText());
                var firmware = await Tool("firmware-b-tpm", "hyperv_firmware", new { machine = b, secureBootTemplate = "MicrosoftUEFICertificateAuthority", tpm = true });
                Expect(firmware.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && firmware.GetProperty("security").GetProperty("KeyProtector").GetBoolean()
                    && firmware.GetProperty("firmware").GetProperty("SecureBootTemplate").GetString() == "MicrosoftUEFICertificateAuthority"
                    && firmware.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("Created a local key protector")), firmware.GetRawText());
                return "local key protector created, vTPM enabled, UEFI CA template";
            });

            await Run("create iso source: DVD added after New-VM and booted first", async () =>
            {
                var iso = Path.Combine(folder, "test.iso");
                WriteIso(iso, Path.Combine(folder, "iso-content"));
                var vm = await Tool("create-iso", "hyperv_vm_create", new { name = e, path = folder, source = "iso", isoPath = iso, diskGb = 1, memoryMb = 512, tpm = false, switchName = "" });
                var order = vm.GetProperty("Firmware").GetProperty("BootOrder");
                Expect(order[0].GetProperty("Device").GetString()!.Equals(iso, StringComparison.OrdinalIgnoreCase) && order[1].GetProperty("BootType").GetString() == "Drive", vm.GetRawText());
                var removed = await Tool("remove-iso-vm", "hyperv_vm_remove", new { machine = e, deleteDisks = true });
                Expect(removed.GetProperty("deleted").GetArrayLength() == 1 && File.Exists(iso)
                    && removed.GetProperty("kept").EnumerateArray().Any(k => k.GetProperty("reason").GetString()!.Contains("ISO")), removed.GetRawText());
                return "boot order DVD, disk, network; removal deleted the disk and kept the ISO";
            });

            await Run("create existing_vhd and linked sources", async () =>
            {
                var attached = await Tool("create-existing", "hyperv_vm_create", new { name = f, path = folder, source = "existing_vhd", vhdPath = data, memoryMb = 512, tpm = false, switchName = "" });
                Expect(attached.GetProperty("createdFiles").GetArrayLength() == 0 && attached.GetProperty("Firmware").GetProperty("BootOrder")[0].GetProperty("Device").GetString()!.Equals(data, StringComparison.OrdinalIgnoreCase), attached.GetRawText());
                var kept = await Tool("remove-existing-keep-disks", "hyperv_vm_remove", new { machine = f });
                Expect(kept.GetProperty("kept")[0].GetProperty("reason").GetString() == "deleteDisks is false" && File.Exists(data), kept.GetRawText());
                var linked = await Tool("create-linked", "hyperv_vm_create", new { name = g, path = folder, source = "linked", parentPath = data, memoryMb = 512, tpm = false, switchName = "" });
                var linkedDisk = linked.GetProperty("createdFiles")[0].GetString()!;
                var parent = await Tool("vhd-inspect-linked-parent", "hyperv_vhd", new { action = "inspect", path = data });
                Expect(parent.GetProperty("readOnly").GetBoolean() && parent.GetProperty("parentOf")[0].GetProperty("vmName").GetString() == g, parent.GetRawText());
                var removed = await Tool("remove-linked", "hyperv_vm_remove", new { machine = g, deleteDisks = true });
                Expect(removed.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(linkedDisk) && File.Exists(data), removed.GetRawText());
                return "existing disk attached and kept on removal; linked child created over the read-only parent and deleted with its VM";
            });

            await Run("full clone: export, import with a new ID, removal", async () =>
            {
                var full = await Tool("clone-full", "hyperv_vm_clone", new { machine = a, newName = h, mode = "full", destination = folder });
                var copy = full.GetProperty("createdFiles")[0].GetString()!;
                var source = Machines(await call("hyperv_list")).Single(m => m.Name == a);
                Expect(full.GetProperty("mode").GetString() == "full" && full.GetProperty("Name").GetString() == h && full.GetProperty("Id").GetString() != source.Id
                    && full.GetProperty("State").GetString() == "Off" && copy.StartsWith(Path.Combine(folder, h), StringComparison.OrdinalIgnoreCase) && File.Exists(copy)
                    && !Directory.EnumerateDirectories(folder, ".hyperv-control-export-*").Any(), full.GetRawText());
                var removed = await Tool("remove-full-clone", "hyperv_vm_remove", new { machine = h, deleteDisks = true });
                Expect(removed.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(copy) && File.Exists(diskA), removed.GetRawText());
                return "copied into its own folder with a new ID; the temporary export was removed; the copy was deleted with its VM";
            });

            string? child = null;
            await Run("linked clone", async () =>
            {
                var clone = await Tool("clone-linked", "hyperv_vm_clone", new { machine = a, newName = c, mode = "linked", destination = folder });
                child = clone.GetProperty("createdFiles")[0].GetString();
                var inspect = await Tool("vhd-inspect-child", "hyperv_vhd", new { action = "inspect", path = child });
                var parent = await Tool("vhd-inspect-parent", "hyperv_vhd", new { action = "inspect", path = diskA });
                Expect(clone.GetProperty("mode").GetString() == "linked" && clone.GetProperty("State").GetString() == "Off" && clone.GetProperty("ProcessorCount").GetInt32() == 4
                    && clone.GetProperty("readOnlyParents")[0].GetString()!.Equals(diskA, StringComparison.OrdinalIgnoreCase)
                    && inspect.GetProperty("chain").GetArrayLength() == 2 && inspect.GetProperty("type").GetString() == "Differencing"
                    && parent.GetProperty("readOnly").GetBoolean() && parent.GetProperty("parentOf").GetArrayLength() == 1
                    && parent.GetProperty("attachedTo")[0].GetProperty("vmName").GetString() == a, clone.GetRawText() + inspect.GetRawText());
                var info = await Tool("info-clone", "hyperv_vm_info", new { machine = c });
                Expect(info.GetProperty("security").GetProperty("TpmEnabled").GetBoolean() && info.GetProperty("disks")[0].GetProperty("Chain").GetArrayLength() == 2
                    && info.GetProperty("firmware").GetProperty("BootOrder")[0].GetProperty("BootType").GetString() == "Drive"
                    && info.GetProperty("memory").GetProperty("dynamicMemoryEnabled").GetBoolean() == false, info.GetRawText());
                return child!;
            });

            await Run("linked clone boots to its firmware", async () =>
            {
                var started = await Tool("start-clone", "hyperv_power", new { machine = c, action = "start" });
                var stopped = await Tool("turn-off-clone", "hyperv_power", new { machine = c, action = "turn_off" });
                Expect(started.GetProperty("machine").GetProperty("state").GetString() == "running" && stopped.GetProperty("machine").GetProperty("state").GetString() == "off", started.GetRawText());
                return "started on a differencing disk over the read-only parent";
            });

            await Run("remove dryRun keeps the clone parent and plans the clone's own disk", async () =>
            {
                var parentPlan = (await Tool("remove-a-dryrun", "hyperv_vm_remove", new { machine = a, deleteDisks = true, dryRun = true })).GetProperty("plan");
                var childPlan = (await Tool("remove-clone-dryrun", "hyperv_vm_remove", new { machine = c, deleteDisks = true, dryRun = true })).GetProperty("plan");
                Expect(parentPlan.GetProperty("delete").GetArrayLength() == 0 && parentPlan.GetProperty("keep")[0].GetProperty("reason").GetString()!.Contains("differencing parent")
                    && childPlan.GetProperty("delete")[0].GetString()!.Equals(child, StringComparison.OrdinalIgnoreCase) && File.Exists(diskA) && File.Exists(child),
                    parentPlan.GetRawText() + childPlan.GetRawText());
                return parentPlan.GetProperty("keep")[0].GetProperty("reason").GetString()!;
            });

            await Run("remove with deleteDisks merges checkpoints first", async () =>
            {
                await Tool("create-d", "hyperv_vm_create", new { name = d, path = folder, source = "new_disk", diskGb = 1, memoryMb = 512, tpm = false, switchName = "" });
                await Tool("checkpoint-d-1", "hyperv_checkpoint", new { machine = d, action = "create", checkpoint = "one" });
                await Tool("checkpoint-d-2", "hyperv_checkpoint", new { machine = d, action = "create", checkpoint = "two" });
                var before = await Tool("info-d-checkpoints", "hyperv_vm_info", new { machine = d });
                var chain = before.GetProperty("disks")[0].GetProperty("Chain").EnumerateArray().Select(l => l.GetProperty("Path").GetString()!).ToList();
                var plan = (await Tool("remove-d-dryrun", "hyperv_vm_remove", new { machine = d, deleteDisks = true, dryRun = true })).GetProperty("plan");
                Expect(before.GetProperty("checkpointCount").GetInt32() == 2 && chain.Count == 3 && plan.GetProperty("checkpoints").GetInt32() == 2
                    && plan.GetProperty("merge").GetArrayLength() == 2 && plan.GetProperty("delete").GetArrayLength() == 1, before.GetRawText() + plan.GetRawText());
                var removed = await Tool("remove-d", "hyperv_vm_remove", new { machine = d, deleteDisks = true });
                Expect(removed.GetProperty("checkpointsRemoved").GetInt32() == 2 && removed.GetProperty("deleted").GetArrayLength() == 1
                    && chain.All(path => !File.Exists(path)), removed.GetRawText());
                return "2 checkpoints merged, " + chain.Count + " chain files gone";
            });

            await Run("remove clone, then its former parent, with deleteDisks", async () =>
            {
                var clone = await Tool("remove-clone", "hyperv_vm_remove", new { machine = c, deleteDisks = true });
                var parent = await Tool("remove-a", "hyperv_vm_remove", new { machine = a, deleteDisks = true });
                var vmB = await Tool("remove-b", "hyperv_vm_remove", new { machine = b, deleteDisks = true });
                Expect(clone.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(child) && parent.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(diskA)
                    && vmB.GetProperty("success").GetBoolean() && parent.GetProperty("removedDirectories").GetArrayLength() == 1, clone.GetRawText() + parent.GetRawText());
                return "clone disk, then the read-only parent deleted; VM folder removed";
            });

            await Run("vhd delete dryRun then delete", async () =>
            {
                var plan = await Tool("vhd-delete-dryrun", "hyperv_vhd", new { action = "delete", path = data, dryRun = true });
                var deleted = await Tool("vhd-delete", "hyperv_vhd", new { action = "delete", path = data });
                Expect(plan.GetProperty("deletable").GetBoolean() && deleted.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(data), plan.GetRawText());
                return "deleted";
            });
        }
        finally
        {
            // Clean up anything this run created, clones before their parents; failures are recorded, not thrown.
            for (var pass = 0; pass < 2; pass++)
                foreach (var vm in Machines(await call("hyperv_list")).Where(m => m.Name.StartsWith(Prefix + run, StringComparison.Ordinal)).OrderBy(m => m.Name.EndsWith("-a") ? 1 : 0))
                {
                    if (vm.State != "off") await call("hyperv_power", new { machine = vm.Id, action = "turn_off" }, true, "lifecycle-live-cleanup-off");
                    await call("hyperv_vm_remove", new { machine = vm.Id, deleteDisks = true }, true, "lifecycle-live-cleanup-remove");
                }
            foreach (var vm in Machines(await call("hyperv_list")).Where(m => m.Name.StartsWith(Prefix + run, StringComparison.Ordinal))) leftovers.Add("VM " + vm.Name);
            if (Directory.Exists(folder))
                foreach (var disk in Directory.EnumerateFiles(folder, "*.*vhd*", SearchOption.AllDirectories))
                    await call("hyperv_vhd", new { action = "delete", path = disk }, true, "lifecycle-live-cleanup-vhd");
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add("folder " + folder + ": " + ex.Message); }

            var afterVms = Machines(await call("hyperv_list", label: "lifecycle-live-final-vms"));
            var afterSwitches = (await call("hyperv_network", new { action = "list" }, label: "lifecycle-live-final-switches")).GetRawText();
            var untouched = baselineVms.All(before => afterVms.Any(after => after.Id == before.Id && after.Name == before.Name && after.State == before.State))
                && afterVms.Count == baselineVms.Count && afterSwitches == baselineSwitches;
            steps.Add(new("other VMs and switches unchanged, nothing of this run left", untouched && leftovers.Count == 0,
                untouched ? (leftovers.Count == 0 ? "" : string.Join("; ", leftovers)) : "baseline differs from the final inventory"));
            await File.WriteAllTextAsync(Path.Combine(results, "lifecycle-live-summary.json"), JsonSerializer.Serialize(new
            {
                run, folder, passed = steps.Count(s => s.Passed), failed = steps.Count(s => !s.Passed), steps, leftovers
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        var failures = steps.Where(s => !s.Passed).ToList();
        check(failures.Count == 0, $"Lifecycle live validation: {steps.Count - failures.Count}/{steps.Count} steps passed"
            + (failures.Count == 0 ? "" : "; failed: " + string.Join(" | ", failures.Select(f => f.Name + ": " + f.Detail))));
    }

    private sealed record Machine(string Id, string Name, string State);

    private static List<Machine> Machines(JsonElement inventory) => inventory.GetProperty("machines").EnumerateArray()
        .Select(m => new Machine(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!, m.GetProperty("state").GetString()!)).ToList();

    /// <summary>Builds a small data ISO (no boot image) with the inbox IMAPI2 file-system image COM API.</summary>
    private static void WriteIso(string path, string contentFolder)
    {
        Directory.CreateDirectory(contentFolder);
        File.WriteAllText(Path.Combine(contentFolder, "readme.txt"), "Hyper-V Control lifecycle validation media.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ?? throw new InvalidOperationException("IMAPI2FS is not registered.");
                dynamic image = Activator.CreateInstance(type)!;
                image.FileSystemsToCreate = 3; // ISO 9660 and Joliet
                image.VolumeName = "HVCTEST";
                image.Root.AddTree(contentFolder, false);
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)image.CreateResultImage().ImageStream;
                var buffer = new byte[1 << 16];
                var read = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    using var file = File.Create(path);
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, read);
                        var count = System.Runtime.InteropServices.Marshal.ReadInt32(read);
                        if (count <= 0) break;
                        file.Write(buffer, 0, count);
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(read); }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Could not build the test ISO: " + failure.Message, failure);
    }

    /// <summary>The errorType of a failed tool call, or null when it succeeded.</summary>
    private static string? Failure(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False
            ? result.GetProperty("errorType").GetString() : null;
}
