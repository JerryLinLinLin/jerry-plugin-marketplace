namespace HyperVControl.Services;

/// <summary>File-handle ownership releases automatically when an MCP worker exits.</summary>
internal static class VmLease
{
    internal static FileStream Acquire(Guid id, string purpose)
    {
        var directory = Path.Combine(ControlPaths.DataRoot, "locks");
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, $"{purpose}-{id:D}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException($"Another Hyper-V Control session owns this VM's {purpose} channel. Close that session before taking control.", ex); }
    }
}
