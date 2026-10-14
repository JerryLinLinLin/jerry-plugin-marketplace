using System.Text.Json;

namespace HyperVControl.Tests;

/// <summary>Calls one MCP tool through the harness client: name, arguments, allowError, label.</summary>
internal delegate Task<JsonElement> ToolCall(string name, object? arguments = null, bool allowError = false, string? label = null);

/// <summary>Lifecycle v2 unit tests against the mock module (phase 2).</summary>
internal static class LifecycleV2Tests
{
    internal static Task RunAsync(Action<bool, string> check) => Task.CompletedTask;
}

/// <summary>Image catalog, library and job unit tests (phase 3).</summary>
internal static class ImageTests
{
    internal static Task RunAsync(Action<bool, string> check) => Task.CompletedTask;
}

/// <summary>Live validation of lifecycle v2 on throwaway VMs (phase 2). Requires an elevated host and explicit authorization.</summary>
internal static class LifecycleLiveTests
{
    internal static Task RunAsync(ToolCall call, Action<bool, string> check, string results) => Task.CompletedTask;
}

/// <summary>Live validation of image fetch, library and deploy (phase 3).</summary>
internal static class ImageLiveTests
{
    internal static Task RunAsync(ToolCall call, Action<bool, string> check, string results) => Task.CompletedTask;
}
