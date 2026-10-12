using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HyperVControl.Services;

internal sealed record DebuggerConsoleRequest(string Engine, string[] Arguments, string SymbolPath);

/// <summary>KD requires a real console even when its command streams are redirected.</summary>
internal static class DebuggerConsole
{
    private static readonly Handler IgnoreSignal = _ => true;

    internal static async Task<int> RunAsync()
    {
        // Preserve the parent's pipes before allocating a private, hidden console.
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        var line = await input.ReadLineAsync() ?? throw new IOException("Missing debugger console request.");
        var request = JsonSerializer.Deserialize<DebuggerConsoleRequest>(line, Program.Json)!;
        FreeConsole();
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(GetConsoleWindow(), 0);
        var start = new ProcessStartInfo(request.Engine)
        {
            UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(request.Engine)!
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        start.Environment["_NT_SYMBOL_PATH"] = request.SymbolPath;
        using var debugger = Process.Start(start) ?? throw new InvalidOperationException("Debugger did not start.");
        // Console handlers themselves aren't inherited. The child retains its own handler.
        SetConsoleCtrlHandler(IgnoreSignal, true);
        using var pumps = new CancellationTokenSource();
        var stdout = debugger.StandardOutput.BaseStream.CopyToAsync(output, pumps.Token);
        var stderr = debugger.StandardError.BaseStream.CopyToAsync(error, pumps.Token);
        var forward = Task.Run(async () =>
        {
            try
            {
                while (await input.ReadLineAsync() is { } command)
                { await debugger.StandardInput.WriteLineAsync(command); await debugger.StandardInput.FlushAsync(); }
                debugger.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        await debugger.WaitForExitAsync();
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { pumps.Cancel(); }
        return debugger.ExitCode;
    }

    internal static int Interrupt(int consoleOwnerPid)
    {
        FreeConsole();
        if (!AttachConsole((uint)consoleOwnerPid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            SetConsoleCtrlHandler(IgnoreSignal, true);
            if (!GenerateConsoleCtrlEvent(1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); // CTRL_BREAK_EVENT, this private console only
            Thread.Sleep(300);
            return 0;
        }
        finally { FreeConsole(); }
    }
    private delegate bool Handler(uint signal);
    [DllImport("kernel32", SetLastError=true)] private static extern bool AllocConsole();
    [DllImport("kernel32", SetLastError=true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32")] private static extern bool FreeConsole();
    [DllImport("kernel32")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32")] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32", SetLastError=true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
}
