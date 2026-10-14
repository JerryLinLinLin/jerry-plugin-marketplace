using System.Text.Json;

namespace HyperVControl.Tests;

/// <summary>Calls one MCP tool through the harness client: name, arguments, allowError, label.</summary>
internal delegate Task<JsonElement> ToolCall(string name, object? arguments = null, bool allowError = false, string? label = null);

/// <summary>Image catalog, library and job unit tests (phase 3).</summary>
internal static class ImageTests
{
    internal static Task RunAsync(Action<bool, string> check) => Task.CompletedTask;
}

/// <summary>Golden image build unit tests (phase 3).</summary>
internal static class ImageBuildTests
{
    internal static Task RunAsync(Action<bool, string> check) => Task.CompletedTask;
}

/// <summary>Live validation of image fetch, library and deploy (phase 3).</summary>
internal static class ImageLiveTests
{
    internal static Task RunAsync(ToolCall call, Action<bool, string> check, string results) => Task.CompletedTask;
}

/// <summary>Live validation of golden image builds (phase 3).</summary>
internal static class ImageBuildLiveTests
{
    internal static Task RunAsync(ToolCall call, Action<bool, string> check, string results) => Task.CompletedTask;
}
