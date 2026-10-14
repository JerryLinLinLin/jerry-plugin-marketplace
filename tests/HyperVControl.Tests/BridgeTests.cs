using System.Collections;
using HyperVControl.Services;

namespace HyperVControl.Tests;

/// <summary>The elevation bridge forwards configuration variables to the UAC worker, which otherwise starts with the profile environment.</summary>
internal static class BridgeTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(ElevationBridge.IsForwarded("HYPERV_CONTROL_IMAGE_ROOT") && ElevationBridge.IsForwarded("hyperv_control_data")
            && ElevationBridge.IsForwarded("HYPERV_GUEST_PASSWORD") && ElevationBridge.IsForwarded("_NT_SYMBOL_PATH")
            && !ElevationBridge.IsForwarded("PATH") && !ElevationBridge.IsForwarded("HYPERVCONTROL_X") && !ElevationBridge.IsForwarded("USERPROFILE"),
            "Only HYPERV_CONTROL_*, HYPERV_GUEST_* and _NT_SYMBOL_PATH are forwarded to the elevated worker");

        var environment = new Hashtable
        {
            ["HYPERV_CONTROL_IMAGE_ROOT"] = @"D:\Images with spaces\空间",
            ["HYPERV_CONTROL_GUEST_PASSWORD"] = "p'a\"s$s",
            ["PATH"] = @"C:\Windows",
            ["_NT_SYMBOL_PATH"] = "srv*C:\\symbols*https://msdl.microsoft.com/download/symbols"
        };
        using var pipe = new MemoryStream();
        await ElevationBridge.WriteEnvironmentAsync(pipe, environment, default);
        pipe.Write("{\"jsonrpc\":\"2.0\"}"u8);
        pipe.Position = 0;
        var forwarded = await ElevationBridge.ReadEnvironmentAsync(pipe, default);
        var rest = new byte[pipe.Length - pipe.Position];
        await pipe.ReadExactlyAsync(rest);
        check(forwarded.Count == 3 && forwarded.Any(v => v.Name == "HYPERV_CONTROL_IMAGE_ROOT" && v.Value == @"D:\Images with spaces\空间")
            && forwarded.Any(v => v.Name == "HYPERV_CONTROL_GUEST_PASSWORD" && v.Value == "p'a\"s$s") && forwarded.Any(v => v.Name == "_NT_SYMBOL_PATH")
            && !forwarded.Any(v => v.Name == "PATH") && System.Text.Encoding.UTF8.GetString(rest) == "{\"jsonrpc\":\"2.0\"}",
            "Environment header round-trips Unicode and quoting and leaves the MCP stream untouched behind it");

        using var bad = new MemoryStream(BitConverter.GetBytes(2_000_000).Concat(new byte[8]).ToArray());
        try { await ElevationBridge.ReadEnvironmentAsync(bad, default); check(false, "Oversized bridge header rejected"); }
        catch (InvalidOperationException) { check(true, "Oversized bridge header rejected"); }
    }
}
