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

        var inventory = await service.HyperV.GetInventoryAsync();
        check(inventory.Capability.IsAvailable && inventory.Machines.Count == 4 && inventory.Machines.Any(m => m.Id == runningVm && m.State == Extracted.HyperVMachineState.Running),
            "Typed inventory reaches the mock module through the shared bootstrap");

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
        var removeRunning = await Expect(() => service.Lifecycle.RemoveAsync(runningVm, default));
        check(removeRunning?.ErrorType == "InvalidVmState" && !mock.Called("Remove-VM"), "Remove on a Running VM is refused without calling Remove-VM");
        mock.ClearCalls();
        var removed = await service.Lifecycle.RemoveAsync(offVm, default);
        var removeCall = mock.Calls().Single(c => c.Cmdlet == "Remove-VM");
        check(removed.GetProperty("success").GetBoolean() && removeCall.Parameters.GetProperty("Force").GetBoolean()
            && removeCall.Parameters.GetProperty("VM").GetString() == offVm.ToString("D") && (await service.HyperV.GetInventoryAsync()).Machines.Count == 3,
            "Remove on an Off VM runs Remove-VM -Force for that VM only");

        mock.ClearCalls();
        var name = "Lab 'quoted' \u7a7a\u95f4; $pwn = 1";
        var created = await service.Lifecycle.CreateAsync(name, @"C:\MockVMs", @"C:\MockVHDs\new.vhdx", 2, 2048, 2, 20, "Default Switch", default);
        var newVm = mock.Calls().Single(c => c.Cmdlet == "New-VM").Parameters;
        check(created.GetProperty("Name").GetString() == name && newVm.GetProperty("Name").GetString() == name
            && newVm.GetProperty("Generation").GetInt32() == 2 && newVm.GetProperty("MemoryStartupBytes").GetInt64() == 2048L * 1024 * 1024
            && newVm.GetProperty("NewVHDPath").GetString() == @"C:\MockVHDs\new.vhdx" && newVm.GetProperty("NewVHDSizeBytes").GetInt64() == 20L * 1024 * 1024 * 1024
            && newVm.GetProperty("SwitchName").GetString() == "Default Switch" && !newVm.TryGetProperty("VHDPath", out _)
            && mock.Calls().Any(c => c.Cmdlet == "Set-VMProcessor" && c.Parameters.GetProperty("Count").GetInt32() == 2),
            "Create renders typed New-VM parameters, omits unused ones, and preserves quotes and Unicode in the name as data");

        var info = await service.Lifecycle.InfoAsync("Lab B", default);
        check(info.GetProperty("vm").GetProperty("Name").GetString() == "Lab B" && info.GetProperty("network").GetArrayLength() == 1
            && info.GetProperty("disks").GetArrayLength() == 1 && info.GetProperty("integration").GetArrayLength() == 6 && info.GetProperty("firmware").GetProperty("SecureBoot").GetString() == "On",
            "Info projection assembles firmware, network, disks and integration services in one process");

        var tools = new HyperVTools(service);
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
    }

    private static async Task<HyperVOperationException?> Expect(Func<Task> action)
    {
        try { await action(); return null; }
        catch (HyperVOperationException ex) { return ex; }
    }
}
