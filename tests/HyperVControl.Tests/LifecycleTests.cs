using System.Text.Json;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;

namespace HyperVControl.Tests;

/// <summary>
/// VM lifecycle behavior against the recording mock module: resolution, state gating, parameter
/// rendering, the uniform error envelope and the typed inventory path. No Hyper-V host is touched.
/// </summary>
internal static class LifecycleTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var offVm = Guid.NewGuid(); var runningVm = Guid.NewGuid(); var duplicateA = Guid.NewGuid(); var duplicateB = Guid.NewGuid();
        using var mock = new MockHyperV(new
        {
            vms = new object[]
            {
                new { id = offVm, name = "Lab A", state = "Off", generation = 2, processorCount = 2, memoryStartup = 4294967296L, dynamicMemory = false, disks = new[] { @"C:\MockVHDs\lab-a.vhdx" }, snapshots = Array.Empty<object>() },
                new { id = runningVm, name = "Lab B", state = "Running", generation = 2, processorCount = 4, memoryStartup = 2147483648L, dynamicMemory = true, disks = new[] { @"C:\MockVHDs\lab-b.vhdx" }, snapshots = Array.Empty<object>() },
                new { id = duplicateA, name = "Dup", state = "Off", generation = 2, processorCount = 1, memoryStartup = 1073741824L, dynamicMemory = false, disks = Array.Empty<string>(), snapshots = Array.Empty<object>() },
                new { id = duplicateB, name = "Dup", state = "Off", generation = 1, processorCount = 1, memoryStartup = 1073741824L, dynamicMemory = false, disks = Array.Empty<string>(), snapshots = Array.Empty<object>() }
            },
            switches = new object[] { new { id = Guid.Parse("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444"), name = "Default Switch", type = "Internal" } }
        });
        using var service = new ControlService();
        var tools = new HyperVTools(service);
        static JsonElement Body(CallToolResult result) => JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

        var inventory = await service.HyperV.GetInventoryAsync();
        check(inventory.Capability.IsAvailable && inventory.Machines.Count == 4 && inventory.Machines.Any(m => m.Id == runningVm && m.State == Extracted.HyperVMachineState.Running),
            "Typed inventory reaches the mock module through the shared bootstrap");

        // Hyper-V's Get-VMSnapshot -ParentOf throws a NullReferenceException for a VM without a current checkpoint (the mock
        // reproduces it), so hyperv_checkpoints reads the VM's ParentSnapshotId instead.
        var parentOf = await Expect(() => service.PowerShell.InvokeAsync(new HyperVScript().Vm("Lab A")
            .Line("$null = Hyper-V\\Get-VMSnapshot -ParentOf $vm -ErrorAction Stop").Returns("$true"), default));
        check(parentOf?.Message == "Object reference not set to an instance of an object.",
            "The mock reproduces Hyper-V: Get-VMSnapshot -ParentOf on a VM without checkpoints throws a NullReferenceException");
        mock.ClearCalls();
        var none = await tools.Checkpoints("Lab A", default);
        var noneBody = Body(none);
        check(none.IsError != true && noneBody.GetProperty("success").GetBoolean() && noneBody.GetProperty("checkpoints").GetArrayLength() == 0
            && noneBody.GetProperty("currentParentCheckpointId").ValueKind == JsonValueKind.Null
            && mock.Called("Get-VMSnapshot") && !mock.Calls().Any(c => c.Cmdlet == "Get-VMSnapshot" && c.Parameters.TryGetProperty("ParentOf", out _)),
            "hyperv_checkpoints on a VM with no checkpoints succeeds with an empty list and no current checkpoint");

        var baseline = Body(await tools.Checkpoint("Lab A", "create", checkpoint: "baseline"));
        var baselineId = baseline.GetProperty("checkpoint").GetProperty("id").GetString();
        var one = await tools.Checkpoints("Lab A", default);
        var oneBody = Body(one);
        var listed = oneBody.GetProperty("checkpoints");
        check(baseline.GetProperty("success").GetBoolean() && one.IsError != true && oneBody.GetProperty("success").GetBoolean() && listed.GetArrayLength() == 1
            && listed[0].GetProperty("id").GetString() == baselineId && listed[0].GetProperty("name").GetString() == "baseline"
            && oneBody.GetProperty("currentParentCheckpointId").GetString() == baselineId,
            "After one checkpoint is created, hyperv_checkpoints lists it and reports it as the current parent checkpoint");

        mock.ClearCalls();
        var restoredCurrent = await tools.Checkpoint("Lab A", "restore", checkpoint: "current");
        var deleted = await tools.Checkpoint("Lab A", "remove", checkpoint: "baseline");
        var cleared = Body(await tools.Checkpoints("Lab A", default));
        check(restoredCurrent.IsError != true && deleted.IsError != true
            && mock.Calls().Single(c => c.Cmdlet == "Restore-VMSnapshot").Parameters.GetProperty("VMSnapshot").GetString() == baselineId
            && cleared.GetProperty("success").GetBoolean() && cleared.GetProperty("checkpoints").GetArrayLength() == 0
            && cleared.GetProperty("currentParentCheckpointId").ValueKind == JsonValueKind.Null,
            "checkpoint=current resolves to that checkpoint; once the last checkpoint is removed the VM lists none and has no current checkpoint");

        mock.ClearCalls();
        var configured = await service.Lifecycle.ConfigureAsync("Lab A", 4, null, null, null, null, default);
        var setProcessor = mock.Calls().Where(c => c.Cmdlet == "Set-VMProcessor").ToList();
        check(configured.GetProperty("ProcessorCount").GetInt32() == 4 && setProcessor.Count == 1
            && setProcessor[0].Parameters.GetProperty("Count").GetInt32() == 4 && setProcessor[0].Parameters.GetProperty("VM").GetString() == offVm.ToString("D"),
            "Configure on an Off VM resolves by name in-script, runs Set-VMProcessor and returns the refreshed projection");

        mock.ClearCalls();
        var refused = await Expect(() => service.Lifecycle.ConfigureAsync("Lab B", 2, null, null, null, null, default));
        check(refused?.ErrorType == "InvalidVmState" && refused.Message.Contains("Off") && !mock.Calls().Any(c => c.Cmdlet.StartsWith("Set-")),
            "Configure on a Running VM fails with InvalidVmState before any mutation");

        var ambiguous = await Expect(() => service.Lifecycle.InfoAsync("Dup", default));
        var missing = await Expect(() => service.Lifecycle.InfoAsync("no-such-vm", default));
        var missingId = await Expect(() => service.Lifecycle.InfoAsync(Guid.NewGuid().ToString("D"), default));
        check(ambiguous?.ErrorType == "AmbiguousName" && missing?.ErrorType == "NotFound" && missingId?.ErrorType == "NotFound",
            "Ambiguous names, unknown names and unknown GUIDs classify as AmbiguousName / NotFound");

        mock.ClearCalls();
        var removeRunning = await Expect(() => service.Lifecycle.RemoveAsync(runningVm.ToString("D"), false, false, default));
        check(removeRunning?.ErrorType == "InvalidVmState" && !mock.Called("Remove-VM"), "Remove on a Running VM is refused without calling Remove-VM");
        mock.ClearCalls();
        var removed = await service.Lifecycle.RemoveAsync(offVm.ToString("D"), false, false, default);
        var removeCall = mock.Calls().Single(c => c.Cmdlet == "Remove-VM");
        check(removed.GetProperty("success").GetBoolean() && removeCall.Parameters.GetProperty("Force").GetBoolean()
            && removeCall.Parameters.GetProperty("VM").GetString() == offVm.ToString("D") && (await service.HyperV.GetInventoryAsync()).Machines.Count == 3,
            "Remove on an Off VM runs Remove-VM -Force for that VM only");

        // Create makes the VM and disk folders, so it runs against a temporary folder.
        mock.ClearCalls();
        var name = "Lab 'quoted' \u7a7a\u95f4; $pwn = 1";
        var temp = Path.Combine(Path.GetTempPath(), "HyperVControl-create-" + Guid.NewGuid().ToString("N")[..8]);
        var vhd = Path.Combine(temp, "VHDs", "new.vhdx");
        try
        {
            var created = await service.Lifecycle.CreateAsync(new VmCreateRequest(name)
            { Path = Path.Combine(temp, "VMs"), VhdPath = vhd, MemoryMb = 2048, DiskGb = 2, SwitchName = "Default Switch" }, default);
            var newVm = mock.Calls().Single(c => c.Cmdlet == "New-VM").Parameters;
            check(created.GetProperty("Name").GetString() == name && newVm.GetProperty("Name").GetString() == name
                && newVm.GetProperty("Generation").GetInt32() == 2 && newVm.GetProperty("MemoryStartupBytes").GetInt64() == 2048L * 1024 * 1024
                && newVm.GetProperty("NewVHDPath").GetString() == vhd && newVm.GetProperty("NewVHDSizeBytes").GetInt64() == 2L * 1024 * 1024 * 1024
                && newVm.GetProperty("SwitchName").GetString() == "Default Switch" && !newVm.TryGetProperty("VHDPath", out _)
                && mock.Calls().Any(c => c.Cmdlet == "Set-VMProcessor" && c.Parameters.GetProperty("Count").GetInt32() == 2),
                "Create renders typed New-VM parameters, omits unused ones, and preserves quotes and Unicode in the name as data");
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

        var info = await service.Lifecycle.InfoAsync("Lab B", default);
        check(info.GetProperty("vm").GetProperty("Name").GetString() == "Lab B" && info.GetProperty("network").GetArrayLength() == 1
            && info.GetProperty("disks").GetArrayLength() == 1 && info.GetProperty("integration").GetArrayLength() == 6 && info.GetProperty("firmware").GetProperty("SecureBoot").GetString() == "On",
            "Info projection assembles firmware, network, disks and integration services in one process");

        var failure = await tools.ConfigureVm("Lab B", processors: 2);
        using (var document = JsonDocument.Parse(failure.Content.OfType<TextContentBlock>().Single().Text))
            check(failure.IsError == true && document.RootElement.GetProperty("ok").ValueKind == JsonValueKind.False
                && document.RootElement.GetProperty("errorType").GetString() == "InvalidVmState" && document.RootElement.GetProperty("error").GetString()!.Length > 0,
                "Tool failures carry the uniform ok/error/errorType envelope");

        mock.ClearCalls();
        var power = await tools.Power("Lab B", "shutdown", default);
        using (var document = JsonDocument.Parse(power.Content.OfType<TextContentBlock>().Single().Text))
            check(power.IsError != true && document.RootElement.GetProperty("success").GetBoolean()
                && document.RootElement.GetProperty("machine").GetProperty("state").GetString() == "off" && mock.Called("Stop-VM"),
                "Typed power operations run through the same mock module and report the updated state");

        var unknownAction = await tools.Power("Lab B", "delete_everything", default);
        using (var document = JsonDocument.Parse(unknownAction.Content.OfType<TextContentBlock>().Single().Text))
            check(unknownAction.IsError == true && document.RootElement.GetProperty("ok").ValueKind == JsonValueKind.False
                && document.RootElement.GetProperty("errorType").GetString() == "ArgumentException",
                "Argument errors keep the same envelope with the exception type as errorType");

        // Export clears the read-only attribute on chain parents for the duration of the export and restores it (Export-VM fails otherwise).
        var exportDir = Path.Combine(Path.GetTempPath(), "HyperVControl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDir);
        var parentDisk = Path.Combine(exportDir, "parent.vhdx"); var childDisk = Path.Combine(exportDir, "child.vhdx");
        File.WriteAllBytes(parentDisk, new byte[16]); File.WriteAllBytes(childDisk, new byte[16]);
        File.SetAttributes(parentDisk, File.GetAttributes(parentDisk) | FileAttributes.ReadOnly);
        var linkedVm = Guid.NewGuid();
        using (var exportMock = new MockHyperV(new
        {
            vms = new object[] { new { id = linkedVm, name = "Lab Linked", state = "Off", generation = 2, processorCount = 2, memoryStartup = 1073741824L, dynamicMemory = false, disks = new[] { childDisk }, snapshots = Array.Empty<object>() } },
            switches = Array.Empty<object>(),
            vhds = new object[] { new { path = childDisk, type = "Differencing", parentPath = parentDisk, size = 2147483648L }, new { path = parentDisk, type = "Dynamic", size = 2147483648L } }
        }))
        using (var exportService = new ControlService())
        {
            var exported = await exportService.Lifecycle.ExportAsync("Lab Linked", Path.Combine(exportDir, "out"), default);
            var restored = exported.GetProperty("readOnlyParentsRestored").EnumerateArray().Select(p => p.GetString()).ToList();
            check(exported.GetProperty("success").GetBoolean() && exportMock.Called("Export-VM") && restored.Count == 1
                && string.Equals(restored[0], parentDisk, StringComparison.OrdinalIgnoreCase) && File.GetAttributes(parentDisk).HasFlag(FileAttributes.ReadOnly),
                "Export clears a read-only differencing parent for Export-VM and restores the attribute afterwards");
        }
        File.SetAttributes(parentDisk, FileAttributes.Normal);
        try { Directory.Delete(exportDir, true); } catch (IOException) { }
    }

    private static async Task<HyperVOperationException?> Expect(Func<Task> action)
    {
        try { await action(); return null; }
        catch (HyperVOperationException ex) { return ex; }
    }
}
