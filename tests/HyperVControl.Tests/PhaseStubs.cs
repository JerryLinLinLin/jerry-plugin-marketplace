using System.Text.Json;

namespace HyperVControl.Tests;

/// <summary>Calls one MCP tool through the harness client: name, arguments, allowError, label.</summary>
internal delegate Task<JsonElement> ToolCall(string name, object? arguments = null, bool allowError = false, string? label = null);
