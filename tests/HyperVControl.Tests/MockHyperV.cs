using System.Text.Json;
using HyperVControl.Services;

namespace HyperVControl.Tests;

/// <summary>
/// Stages the recording mock of the Hyper-V module and points the server at it through the
/// environment the spawned PowerShell processes inherit. One instance per scenario; dispose to
/// restore the real module for later tests.
/// </summary>
internal sealed class MockHyperV : IDisposable
{
    private const string StateVariable = "HYPERV_CONTROL_MOCK_STATE";
    private const string LogVariable = "HYPERV_CONTROL_MOCK_LOG";

    public string Directory { get; }
    public string StatePath { get; }
    public string LogPath { get; }
    public string ManifestPath { get; }

    public MockHyperV(object state)
    {
        Directory = Path.Combine(Path.GetTempPath(), "HyperVControl-mock-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(Directory, "Hyper-V");
        Copy(Path.Combine(AppContext.BaseDirectory, "MockHyperV", "Hyper-V"), module);
        ManifestPath = Path.Combine(module, "Hyper-V.psd1");
        StatePath = Path.Combine(Directory, "state.json");
        LogPath = Path.Combine(Directory, "calls.jsonl");
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Environment.SetEnvironmentVariable(HyperVScript.ModulePathVariable, ManifestPath);
        Environment.SetEnvironmentVariable(StateVariable, StatePath);
        Environment.SetEnvironmentVariable(LogVariable, LogPath);
    }

    /// <summary>Every cmdlet invocation recorded since the last <see cref="ClearCalls"/>, in order.</summary>
    public IReadOnlyList<(string Cmdlet, JsonElement Parameters)> Calls() => !File.Exists(LogPath) ? [] :
        File.ReadAllLines(LogPath).Where(line => line.Length > 0).Select(line =>
        {
            var entry = JsonDocument.Parse(line).RootElement.Clone();
            return (entry.GetProperty("cmdlet").GetString()!, entry.GetProperty("parameters"));
        }).ToList();

    public bool Called(string cmdlet) => Calls().Any(call => call.Cmdlet == cmdlet);

    public void ClearCalls() { if (File.Exists(LogPath)) File.Delete(LogPath); }

    public JsonElement State() => JsonDocument.Parse(File.ReadAllText(StatePath)).RootElement.Clone();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HyperVScript.ModulePathVariable, null);
        Environment.SetEnvironmentVariable(StateVariable, null);
        Environment.SetEnvironmentVariable(LogVariable, null);
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Copy(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in System.IO.Directory.GetDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
