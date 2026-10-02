using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HyperVControl.Services;

internal static class ElevationBridge
{
    internal static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static async Task RunAsync()
    {
        var pipeName = "HyperVControl-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the elevated worker.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await pipe.WaitForConnectionAsync(timeout.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var client) || client != worker.Id)
            throw new InvalidOperationException("Unexpected elevation bridge client.");
        using var stop = new CancellationTokenSource();
        var input = Console.OpenStandardInput().CopyToAsync(pipe, stop.Token);
        var output = pipe.CopyToAsync(Console.OpenStandardOutput(), stop.Token);
        await Task.WhenAny(input, output, worker.WaitForExitAsync());
        if (worker.HasExited && worker.ExitCode != 0)
            throw new InvalidOperationException($"Elevated worker exited with code {worker.ExitCode}. See {Path.Combine(ControlPaths.DataRoot, "worker-startup-error.txt")}.");
        if (input.IsCompletedSuccessfully)
        {
            // EOF from an MCP client also closes its worker connection.
            pipe.Disconnect();
        }
        stop.Cancel();
        if (!worker.HasExited)
        {
            try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { worker.Kill(true); }
        }
    }

    internal static async Task<NamedPipeClientStream> ConnectWorkerAsync(string name, int parentId)
    {
        if (!IsAdministrator || !name.StartsWith("HyperVControl-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid elevated worker invocation.");
        // CurrentUserOnly on a CLIENT compares token Owner, which can become the
        // Administrators group after UAC. The server ACL stays CurrentUserOnly;
        // authenticate the actual server process User SID below instead of Owner.
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server) || server != parentId || !IsSameUserProcess(parentId))
        { pipe.Dispose(); throw new InvalidOperationException("Unexpected elevation bridge server."); }
        return pipe;
    }

    private static bool IsSameUserProcess(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (!OpenProcessToken(process.Handle, 0x0008, out var token)) return false;
        try
        {
            using var identity = new WindowsIdentity(token);
            using var current = WindowsIdentity.GetCurrent();
            return identity.User == current.User;
        }
        finally { CloseHandle(token); }
    }

    [DllImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);
    [DllImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint id);
    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint id);
}
