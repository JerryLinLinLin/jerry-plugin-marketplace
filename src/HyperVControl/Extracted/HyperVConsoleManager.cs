// Extracted and adapted from RaivenX b5971dda1fa2050dde36b8b22fda9726df92cb48. See THIRD-PARTY-NOTICES.md.
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HyperVControl.Extracted;

namespace HyperVControl.Extracted;

/// <summary>
/// Owns the elevated RDP ActiveX window used by Hyper-V Basic and Enhanced Session
/// consoles. The window is parented into a native placeholder created by the app.
/// </summary>
internal sealed class HyperVConsoleManager : IDisposable
{
    /// <summary>
    /// Ceiling on simultaneously open consoles for one chat. Each console is a real RDP session
    /// into a VM plus an ActiveX instance on the single console thread, so "as many as you like"
    /// is an invitation to open every VM on the host. Four covers the real multi-machine
    /// workflows (attacker, victim, DC, jump box) with room to spare.
    /// </summary>
    internal const int MaximumConsolesPerOwner = 4;

    private readonly TaskCompletionSource<SynchronizationContext> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    /// <summary>
    /// Keyed by owner AND machine, not owner alone: one chat may keep several machines
    /// connected at once, each in its own tab. Under the old owner-only key, opening a second
    /// machine silently disposed the first tab's console.
    /// </summary>
    private readonly ConcurrentDictionary<ConsoleKey, ConsoleWindow> _windows = new();
    private bool _disposed;

    private readonly record struct ConsoleKey(string OwnerId, Guid MachineId);

    public HyperVConsoleManager()
    {
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Hyper-V console",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool HasActiveConsole =>
        _windows.Values.Any(static window => !window.IsDisposed);

    public bool TryGetParentWindow(
        string consoleOwnerId,
        Guid machineId,
        out nint parentWindow)
    {
        if (_windows.TryGetValue(new ConsoleKey(consoleOwnerId, machineId), out var window)
            && !window.IsDisposed)
        {
            parentWindow = window.ParentWindow;
            return true;
        }

        parentWindow = nint.Zero;
        return false;
    }

    public async Task<HyperVConsoleOperationResult> OpenAsync(
        string consoleOwnerId,
        Guid machineId,
        nint parentWindow,
        string hostName,
        bool useEnhancedSession,
        int displayScalePercent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion =
            new TaskCompletionSource<HyperVConsoleOperationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(
            async state =>
            {
                var key = new ConsoleKey(consoleOwnerId, machineId);
                try
                {
                    // Replacing this machine's own console is a reconnect, never counted
                    // against the cap. Other machines' consoles are left strictly alone.
                    if (_windows.TryRemove(key, out var previous))
                    {
                        previous.Dispose();
                    }
                    else if (CountLiveConsoles(consoleOwnerId) >= MaximumConsolesPerOwner)
                    {
                        completion.TrySetResult(new(
                            false,
                            $"This MCP session already has {MaximumConsolesPerOwner} machine consoles "
                            + "open. Close one before connecting another."));
                        return;
                    }

                    var window = new ConsoleWindow(
                        parentWindow,
                        machineId,
                        hostName,
                        useEnhancedSession,
                        displayScalePercent);
                    _windows[key] = window;
                    var result = await window.ConnectAsync();
                    if (!result.Success
                        && _windows.TryGetValue(key, out var current)
                        && ReferenceEquals(current, window))
                    {
                        _windows.TryRemove(key, out _);
                    }

                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    if (_windows.TryRemove(key, out var failed))
                    {
                        failed.Dispose();
                    }

                    completion.TrySetResult(new(false, FirstLine(ex.Message)));
                }
            },
            null);

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HyperVConsoleOperationResult> CloseAsync(
        string consoleOwnerId,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion =
            new TaskCompletionSource<HyperVConsoleOperationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(
            _ =>
            {
                try
                {
                    if (_windows.TryRemove(
                        new ConsoleKey(consoleOwnerId, machineId),
                        out var removed))
                    {
                        removed.Dispose();
                    }

                    completion.TrySetResult(new(true, null));
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new(false, FirstLine(ex.Message)));
                }
            },
            null);

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HyperVConsoleCommandResult> ExecuteAsync(
        string consoleOwnerId,
        Guid machineId,
        HyperVConsoleAction action,
        int? x,
        int? y,
        string? input,
        int? delta,
        string? userName,
        string? password,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion =
            new TaskCompletionSource<HyperVConsoleCommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(
            async _ =>
            {
                try
                {
                    if (!_windows.TryGetValue(
                            new ConsoleKey(consoleOwnerId, machineId),
                            out var window)
                        || window.IsDisposed)
                    {
                        completion.TrySetResult(new(
                            false,
                            "The requested virtual machine has no console open in this MCP session. "
                            + "Run the hyperv_console command with action=open and the machine name first."));
                        return;
                    }

                    if (!window.IsConnected)
                    {
                        completion.TrySetResult(new(
                            false,
                            "The Hyper-V console is not connected yet. Reopen it with the hyperv "
                            + "console command (action=open, machine=<name>), and check the machine "
                            + "is running with the hyperv_list command."));
                        return;
                    }

                    // An Enhanced Session's input and screen copy belong to the Remote Desktop
                    // control, so they run here, on its thread. A Basic Session's actions are
                    // WMI calls into Hyper-V (synthetic keyboard and mouse, the framebuffer)
                    // that need no window and can take seconds for a long text; they run on
                    // the pool so this thread keeps pumping. Its window sits inside the app's
                    // window, which ties the two input queues together: a stall here is a
                    // stall of the app's own mouse and keyboard.
                    var result = window.UsesEnhancedSession || action is HyperVConsoleAction.Show or HyperVConsoleAction.Resize
                        ? window.Execute(action, x, y, input, delta, userName, password)
                        : await Task.Run(() => window.Execute(action, x, y, input, delta, userName, password));
                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new(false, FirstLine(ex.Message)));
                }
            },
            null);

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private int CountLiveConsoles(string consoleOwnerId) =>
        _windows.Count(pair =>
            pair.Key.OwnerId == consoleOwnerId && !pair.Value.IsDisposed);

    private void RunMessageLoop()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        _ready.TrySetResult(context);
        Application.Run();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ready.Task.IsCompletedSuccessfully)
        {
            _ready.Task.Result.Post(
                _ =>
                {
                    foreach (var window in _windows.Values)
                    {
                        window.Dispose();
                    }

                    _windows.Clear();
                    Application.ExitThread();
                },
                null);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static string FirstLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "The Hyper-V console could not be opened."
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    private sealed class ConsoleWindow : Form
    {
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
        private const int GwlStyle = -16;
        private const long WsChild = 0x40000000L;
        private const long WsVisible = 0x10000000L;
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsPopup = unchecked((long)0x80000000);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const int MaximumTextChunkLength = 256;
        private const int HyperVConsolePort = 2179;
        private const int MinimumInputSendIntervalMilliseconds = 20;
        private const int CredentialFailureDisconnectReason = 3848;
        private const string AuthenticationServiceClass = "Microsoft Virtual Console Service";
        private const ushort VkControl = 0x11;
        private const ushort VkMenu = 0x12;
        private const ushort VkShift = 0x10;
        private const ushort VkLwin = 0x5B;
        private const uint MapVkVkToVscEx = 4;
        private static readonly uint[] SupportedDesktopScaleFactors =
            [100u, 125u, 150u, 175u, 200u, 250u, 300u, 400u, 500u];

        private readonly nint _parentWindow;
        private readonly Guid _machineId;
        private readonly string _hostName;
        /// <summary>
        /// Whether this console is running as an Enhanced Session. Requested by the host, but
        /// cleared here when Hyper-V reports the guest cannot support one — see
        /// <see cref="TryEnsureEnhancedSessionAvailable"/>. Every downstream branch (the
        /// preconnection blob, clipboard redirection, and the whole input path) reads this
        /// field, so clearing it before <c>ConfigureAndConnect</c> reconfigures all of them.
        /// </summary>
        private bool _useEnhancedSession;
        /// <summary>Why Enhanced Session was declined, when the console fell back to Basic.</summary>
        private string _enhancedFallbackReason = string.Empty;

        private readonly int _requestedDisplayScalePercent;
        private readonly RemoteDesktopAxHost _activeX;
        private readonly System.Windows.Forms.Timer _timer;
        private ManagementObject? _virtualMachine;
        private ManagementObject? _keyboard;
        private ManagementObject? _mouse;
        private ManagementObject? _videoHead;
        private ManagementObject? _managementService;
        private ManagementObject? _activeSettingData;
        private IMsRdpInputSink? _rdpInputSink;
        private TaskCompletionSource<HyperVConsoleOperationResult>? _connection;
        private DateTimeOffset _connectionDeadline;
        private Size _sessionDisplaySize;
        private uint _sessionDisplayScaleFactor;
        private bool _dynamicResolutionUnavailable;

        public Guid MachineId => _machineId;

        public nint ParentWindow => _parentWindow;

        public ConsoleWindow(
            nint parentWindow,
            Guid machineId,
            string hostName,
            bool useEnhancedSession,
            int displayScalePercent)
        {
            _parentWindow = parentWindow;
            _machineId = machineId;
            _hostName = hostName;
            _useEnhancedSession = useEnhancedSession;
            _requestedDisplayScalePercent = displayScalePercent;
            FormBorderStyle = parentWindow == 0 ? FormBorderStyle.Sizable : FormBorderStyle.None;
            ShowInTaskbar = parentWindow == 0;
            StartPosition = parentWindow == 0 ? FormStartPosition.CenterScreen : FormStartPosition.Manual;
            ClientSize = new Size(1280, 720);
            Text = $"Hyper-V Control — {machineId:D}";

            _activeX = new RemoteDesktopAxHost
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.Black,
            };
            ((ISupportInitialize)_activeX).BeginInit();
            Controls.Add(_activeX);
            ((ISupportInitialize)_activeX).EndInit();
            _activeX.Connected += OnRdpConnected;
            _activeX.Disconnected += OnRdpDisconnected;
            _activeX.FatalError += OnRdpFatalError;
            _activeX.LogonError += OnRdpLogonError;

            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += OnTimerTick;
        }

        public Task<HyperVConsoleOperationResult> ConnectAsync()
        {
            _connection =
                new TaskCompletionSource<HyperVConsoleOperationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                CreateControl();
                _activeX.CreateControl();
                EmbedInParent();
                // A guest that is still booting reports "not ready", and Enhanced Session cannot
                // show a boot or sign-in screen anyway — that is the Basic console's job. Failing
                // the open here would take away the one view that works at exactly the moment the
                // user (or the agent's start-then-attach) needs it, so fall back and connect.
                if (_useEnhancedSession && !TryEnsureEnhancedSessionAvailable(out var enhancedGap))
                {
                    _useEnhancedSession = false;
                    _enhancedFallbackReason = enhancedGap;
                }

                ConfigureAndConnect();
                _connectionDeadline = DateTimeOffset.UtcNow + ConnectionTimeout;
                _timer.Start();
            }
            catch (Exception ex)
            {
                Complete(false, FirstLine(ex.Message));
                Dispose();
            }

            return _connection.Task;
        }

        /// <summary>Whether the Remote Desktop control reports a live session. Read on the console thread.</summary>
        public bool IsConnected => ReadConnectionState() == 1;

        /// <summary>
        /// The mode this console actually connected with (the requested Enhanced Session may
        /// have fallen back to Basic). Fixed once <see cref="ConnectAsync"/> has run.
        /// </summary>
        public bool UsesEnhancedSession => _useEnhancedSession;

        protected override bool ShowWithoutActivation => true;

        /// <summary>
        /// Runs one console action. An Enhanced Session's actions drive the Remote Desktop
        /// control and must run on the console thread; a Basic Session's are WMI calls into
        /// Hyper-V that touch no window and may run on any thread — the manager decides.
        /// </summary>
        public HyperVConsoleCommandResult Execute(
            HyperVConsoleAction action,
            int? x,
            int? y,
            string? input,
            int? delta,
            string? userName,
            string? password)
        {
            return action switch
            {
                HyperVConsoleAction.Capture => CaptureConsole(),
                HyperVConsoleAction.Click =>
                    ClickConsole(x!.Value, y!.Value, input!),
                HyperVConsoleAction.Type => TypeText(input!),
                HyperVConsoleAction.Key => PressKey(input!),
                HyperVConsoleAction.Scroll =>
                    ScrollConsole(x!.Value, y!.Value, delta!.Value),
                HyperVConsoleAction.SignIn => SignIn(userName!, password!),
                HyperVConsoleAction.Move => MoveOrButton(x!.Value, y!.Value, input ?? "left", null),
                HyperVConsoleAction.ButtonDown => MoveOrButton(x!.Value, y!.Value, input ?? "left", true),
                HyperVConsoleAction.ButtonUp => MoveOrButton(x!.Value, y!.Value, input ?? "left", false),
                HyperVConsoleAction.ClipboardRead => ClipboardAction(null),
                HyperVConsoleAction.ClipboardWrite => ClipboardAction(input ?? ""),
                HyperVConsoleAction.Show => ShowConsole(),
                HyperVConsoleAction.Resize => ResizeConsole(x!.Value, y!.Value),
                _ => new(false, "The Hyper-V console action is not supported."),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
                _activeX.Connected -= OnRdpConnected;
                _activeX.Disconnected -= OnRdpDisconnected;
                _activeX.FatalError -= OnRdpFatalError;
                _activeX.LogonError -= OnRdpLogonError;
                Complete(false, "The Hyper-V console was closed.");

                _videoHead?.Dispose();
                _mouse?.Dispose();
                _keyboard?.Dispose();
                _managementService?.Dispose();
                _activeSettingData?.Dispose();
                _virtualMachine?.Dispose();
                _rdpInputSink = null;
                _activeX.Dispose();
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EmbedInParent()
        {
            if (_parentWindow == 0) { Show(); return; }
            if (!IsWindow(_parentWindow))
            {
                throw new InvalidOperationException("The Machine panel is no longer available.");
            }

            var style = GetWindowLongPtr(Handle, GwlStyle).ToInt64();
            style &= ~(WsCaption | WsThickFrame | WsPopup);
            style |= WsChild | WsVisible;
            SetLastError(0);
            var previous = SetWindowLongPtr(Handle, GwlStyle, new nint(style));
            if (previous == nint.Zero && Marshal.GetLastWin32Error() != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            SetLastError(0);
            var oldParent = SetParent(Handle, _parentWindow);
            if (oldParent == nint.Zero && Marshal.GetLastWin32Error() != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ResizeToParent();
        }

        private void ConfigureAndConnect()
        {
            dynamic client = _activeX.ActiveXInstance;
            client.Server = string.Equals(
                _hostName,
                Environment.MachineName,
                StringComparison.OrdinalIgnoreCase)
                    ? "localhost"
                    : _hostName;
            _sessionDisplaySize = NormalizeSessionDisplaySize(ReadParentClientSize());
            _sessionDisplayScaleFactor = ResolveDesktopScaleFactor();
            client.DesktopWidth = _sessionDisplaySize.Width;
            client.DesktopHeight = _sessionDisplaySize.Height;
            client.ColorDepth = 32;

            dynamic advanced6 = client.AdvancedSettings6;
            advanced6.AuthenticationLevel = 0;

            dynamic advanced2 = client.AdvancedSettings2;
            advanced2.RDPPort = HyperVConsolePort;
            advanced2.minInputSendInterval = MinimumInputSendIntervalMilliseconds;

            dynamic advanced7 = client.AdvancedSettings7;
            advanced7.AuthenticationServiceClass = AuthenticationServiceClass;
            advanced7.PCB = _useEnhancedSession
                ? $"{_machineId:D};EnhancedMode=1"
                : _machineId.ToString("D");
            advanced7.RelativeMouseMode = !_useEnhancedSession;
            advanced7.SmartSizing = !_useEnhancedSession;
            advanced7.GrabFocusOnConnect = false;
            if (_useEnhancedSession)
            {
                advanced7.allowBackgroundInput = 1;
            }

            dynamic advanced8 = client.AdvancedSettings8;
            advanced8.EnableCredSspSupport = true;
            advanced8.NegotiateSecurityLayer = false;

            var extended = (IMsRdpExtendedSettings)_activeX.ActiveXInstance;
            object disableCredentialsDelegation = true;
            extended.set_Property(
                "DisableCredentialsDelegation",
                ref disableCredentialsDelegation);

            if (_useEnhancedSession)
            {
                if (_requestedDisplayScalePercent != 0)
                {
                    object desktopScaleFactor = _sessionDisplayScaleFactor;
                    extended.set_Property("DesktopScaleFactor", ref desktopScaleFactor);
                    object deviceScaleFactor =
                        ResolveDeviceScaleFactor(_sessionDisplayScaleFactor);
                    extended.set_Property("DeviceScaleFactor", ref deviceScaleFactor);
                }

                advanced2.RedirectClipboard = true;
            }

            client.SecuredSettings3.KeyboardHookMode = 1;
            client.Connect();
        }

        /// <summary>
        /// Reports whether Hyper-V can give this guest an Enhanced Session right now.
        /// </summary>
        /// <param name="reason">Why not, when the answer is no. Empty when it is yes.</param>
        /// <remarks>
        /// Every "no" here is either transient (a guest that has not finished booting) or a host
        /// configuration the console cannot change, and in both cases a Basic console still shows
        /// the guest. So this answers rather than throws: the caller degrades instead of leaving
        /// the user with no picture at all.
        /// </remarks>
        private bool TryEnsureEnhancedSessionAvailable(out string reason)
        {
            object? rawState;
            try
            {
                rawState = GetVirtualMachine()["EnhancedSessionModeState"];
            }
            catch (Exception ex)
            {
                reason = FirstLine(ex.Message);
                return false;
            }

            if (rawState is null)
            {
                reason =
                    "Hyper-V did not report Enhanced Session availability for this virtual machine.";
                return false;
            }

            var state = Convert.ToUInt16(rawState);
            if (state == 2)
            {
                reason = string.Empty;
                return true;
            }

            reason = state switch
            {
                3 => "Enhanced Session is disabled by the Hyper-V host.",
                6 => "Enhanced Session is enabled, but the guest is not ready for it.",
                _ => $"Enhanced Session is unavailable for this virtual machine (state {state}).",
            };
            return false;
        }

        private HyperVConsoleCommandResult CaptureConsole() =>
            _useEnhancedSession ? CaptureEnhancedScreen() : CaptureBasicFramebuffer();

        /// <summary>
        /// Enhanced Session capture: a copy of REAL screen pixels at the RDP control's
        /// rectangle. The framebuffer API below does not see an Enhanced session's desktop
        /// (verified empirically — the video head shows the console, not the RDP session),
        /// so this mode fundamentally requires the console to be visible and unoccluded;
        /// the app checks that and tells the agent to ask the user when it is not.
        /// </summary>
        private HyperVConsoleCommandResult CaptureEnhancedScreen()
        {
            if (!Visible || WindowState == FormWindowState.Minimized || GetForegroundWindow() != Handle)
                return new(false, "Enhanced capture requires its Hyper-V Control window in the foreground and unobscured. Bring it forward or use a Basic session for headless capture.");
            var size = _activeX.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return new(false, "The Hyper-V console has no visible area.");
            }

            var origin = _activeX.PointToScreen(Point.Empty);
            var rectangle = new Rectangle(origin, size);
            if (!SystemInformation.VirtualScreen.Contains(rectangle))
                return new(false, "The Enhanced console must be entirely on screen.");
            var obscured = false;
            EnumWindows((window, _) =>
            {
                if (window == Handle) return false;
                if (IsWindowVisible(window) && GetWindowRect(window, out var bounds)
                    && rectangle.IntersectsWith(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)))
                    obscured = true;
                return true;
            }, 0);
            if (obscured) return new(false, "Another host window overlaps the Enhanced console; capture would include host pixels.");
            using var bitmap = new Bitmap(
                size.Width,
                size.Height,
                PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    origin,
                    Point.Empty,
                    size,
                    CopyPixelOperation.SourceCopy);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return new(true, null, stream.ToArray(), size.Width, size.Height, IsEnhanced: true);
        }

        /// <summary>
        /// Basic-session capture: the guest's video framebuffer read straight from Hyper-V
        /// (the API Hyper-V Manager's own thumbnails use), at the video head's NATIVE
        /// resolution. No window, no host screen, no focus — a backgrounded chat or an
        /// overlapping window cannot corrupt it — and the returned image's pixels ARE guest
        /// coordinates, which is what fixed hyperv_input clicks: the old screen copy
        /// captured the control's padding around a centered guest desktop while the click
        /// math assumed a full-control stretch, so clicks landed on the wallpaper.
        /// </summary>
        private HyperVConsoleCommandResult CaptureBasicFramebuffer()
        {
            var videoHead = GetVideoHead();
            var width = Convert.ToInt32(videoHead["CurrentHorizontalResolution"]);
            var height = Convert.ToInt32(videoHead["CurrentVerticalResolution"]);
            if (width <= 0 || height <= 0)
            {
                return new(
                    false,
                    "The guest has not initialized its display yet. Wait for the machine "
                    + "to finish booting, then capture again.");
            }

            // One resolved service for both calls: WMI rejects parameters built from a
            // different instance than the one the method is invoked on.
            var service = GetManagementService();
            using var parameters =
                service.GetMethodParameters("GetVirtualSystemThumbnailImage");
            parameters["TargetSystem"] = GetActiveSettingData().Path.Path;
            parameters["WidthPixels"] = (ushort)width;
            parameters["HeightPixels"] = (ushort)height;
            using var result = service.InvokeMethod(
                "GetVirtualSystemThumbnailImage",
                parameters,
                null);

            var returnValue = Convert.ToUInt32(result["ReturnValue"]);
            if (returnValue != 0)
            {
                return new(
                    false,
                    $"Hyper-V could not read the virtual machine's framebuffer (code {returnValue}). "
                    + "Check the machine is running, then capture again.");
            }

            if (result["ImageData"] is not byte[] imageData)
            {
                return new(false, "Hyper-V returned no framebuffer data for this virtual machine.");
            }

            byte[] bgra;
            try
            {
                bgra = Rgb565Pixels.ToBgra32(imageData, width, height);
            }
            catch (ArgumentException ex)
            {
                return new(false, $"Hyper-V returned a malformed framebuffer: {ex.Message}");
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var bounds = new Rectangle(0, 0, width, height);
            var bits = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Stride can exceed width*4; copy row by row so padding never corrupts rows.
                for (var row = 0; row < height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        bgra,
                        row * width * 4,
                        bits.Scan0 + (row * bits.Stride),
                        width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return new(true, null, stream.ToArray(), width, height, IsEnhanced: false);
        }

        private HyperVConsoleCommandResult ClickConsole(int x, int y, string button)
        {
            return _useEnhancedSession
                ? ClickEnhancedConsole(x, y, button)
                : ClickBasicConsole(x, y, button);
        }

        private HyperVConsoleCommandResult ClipboardAction(string? text)
        {
            if (!_useEnhancedSession) return new(false, "Clipboard redirection requires Enhanced Session. Use type or file copy with Basic Session.");
            if (text is not null) { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); return new(true, null); }
            return new(true, null, Text: Clipboard.ContainsText() ? Clipboard.GetText() : "");
        }

        private HyperVConsoleCommandResult ShowConsole()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate(); BringToFront();
            return new(true, null);
        }

        private HyperVConsoleCommandResult ResizeConsole(int width, int height)
        {
            if (width is < 200 or > 8192 || height is < 200 or > 8192) return new(false, "Console dimensions must be 200..8192 pixels.");
            ClientSize = new Size(width, height);
            ResizeToParent();
            return new(true, null);
        }

        private HyperVConsoleCommandResult MoveOrButton(int x, int y, string button, bool? down)
        {
            if (button is not ("left" or "right" or "middle")) return new(false, "Use left, right, or middle.");
            int gx, gy; string? error;
            if (_useEnhancedSession)
            {
                if (!TryResolveRdpPoint(x, y, out gx, out gy, out error)) return new(false, error);
                var sink = GetRdpInputSink();
                var move = ToRdpInputResult(sink.SendMouseMoveEvent((uint)gx, (uint)gy), "move pointer");
                if (!move.Success || down is null) return move;
                var index = button == "left" ? 0 : button == "right" ? 1 : 2;
                return ToRdpInputResult(sink.SendMouseButtonEvent((MouseButtonType)index, down.Value, (uint)gx, (uint)gy), "change mouse button");
            }
            if (!TryResolveGuestPoint(x, y, out gx, out gy, out error)) return new(false, error);
            var mouse = GetMouse();
            var moved = ToCommandResult(InvokeDeviceMethod(mouse, "SetAbsolutePosition", ("horizontalPosition", gx), ("verticalPosition", gy)), "move pointer");
            if (!moved.Success || down is null) return moved;
            return ToCommandResult(InvokeDeviceMethod(mouse, down.Value ? "PressButton" : "ReleaseButton",
                ("buttonIndex", (uint)(button == "left" ? 1 : button == "right" ? 2 : 3))), "change mouse button");
        }

        private HyperVConsoleCommandResult ClickBasicConsole(int x, int y, string button)
        {
            if (!TryResolveGuestPoint(x, y, out var guestX, out var guestY, out var error))
            {
                return new(false, error);
            }

            var mouse = GetMouse();
            var moveResult = ToCommandResult(
                InvokeDeviceMethod(
                    mouse,
                    "SetAbsolutePosition",
                    ("horizontalPosition", guestX),
                    ("verticalPosition", guestY)),
                "move the VM pointer");
            if (!moveResult.Success)
            {
                return moveResult;
            }

            var click = button switch
            {
                "left" => (Button: 1, Count: 1),
                "right" => (Button: 2, Count: 1),
                "double" => (Button: 1, Count: 2),
                _ => (Button: 0, Count: 0),
            };
            if (click.Count == 0)
            {
                return new(false, "Use left, right, or double for the mouse button.");
            }

            for (var count = 0; count < click.Count; count++)
            {
                var result = ToCommandResult(
                    InvokeDeviceMethod(
                        mouse,
                        "ClickButton",
                        ("buttonIndex", (uint)click.Button)),
                    "click in the VM");
                if (!result.Success)
                {
                    return result;
                }
            }

            return new(true, null);
        }

        private HyperVConsoleCommandResult ClickEnhancedConsole(int x, int y, string button)
        {
            if (!TryResolveRdpPoint(x, y, out var guestX, out var guestY, out var error))
            {
                return new(false, error);
            }

            var click = button switch
            {
                "left" => (Button: MouseButtonType.Button1, Count: 1),
                "right" => (Button: MouseButtonType.Button2, Count: 1),
                "double" => (Button: MouseButtonType.Button1, Count: 2),
                _ => (Button: MouseButtonType.Button1, Count: 0),
            };
            if (click.Count == 0)
            {
                return new(false, "Use left, right, or double for the mouse button.");
            }

            var sink = GetRdpInputSink();
            var moveResult = ToRdpInputResult(
                sink.SendMouseMoveEvent((uint)guestX, (uint)guestY),
                "move the pointer in the Enhanced Session");
            if (!moveResult.Success)
            {
                return moveResult;
            }

            for (var count = 0; count < click.Count; count++)
            {
                var downResult = ToRdpInputResult(
                    sink.SendMouseButtonEvent(
                        click.Button,
                        true,
                        (uint)guestX,
                        (uint)guestY),
                    "press the mouse button in the Enhanced Session");
                if (!downResult.Success)
                {
                    return downResult;
                }

                var upResult = ToRdpInputResult(
                    sink.SendMouseButtonEvent(
                        click.Button,
                        false,
                        (uint)guestX,
                        (uint)guestY),
                    "release the mouse button in the Enhanced Session");
                if (!upResult.Success)
                {
                    return upResult;
                }
            }

            return new(true, null);
        }

        private HyperVConsoleCommandResult TypeText(string text)
        {
            return _useEnhancedSession
                ? TypeEnhancedText(text)
                : TypeBasicText(text);
        }

        private HyperVConsoleCommandResult TypeBasicText(string text)
        {
            if (text.Length == 0)
            {
                return new(true, null);
            }

            var keyboard = GetKeyboard();
            for (var offset = 0; offset < text.Length;)
            {
                var length = Math.Min(MaximumTextChunkLength, text.Length - offset);
                if (offset + length < text.Length
                    && char.IsHighSurrogate(text[offset + length - 1]))
                {
                    length--;
                }

                if (length == 0)
                {
                    return new(false, "The text contains an incomplete Unicode character.");
                }

                var result = ToCommandResult(
                    InvokeDeviceMethod(
                        keyboard,
                        "TypeText",
                        ("asciiText", text.Substring(offset, length))),
                    "type text in the VM");
                if (!result.Success)
                {
                    return result;
                }

                offset += length;
            }

            return new(true, null);
        }

        private HyperVConsoleCommandResult TypeEnhancedText(string text)
        {
            var sink = GetRdpInputSink();
            foreach (var codeUnit in text)
            {
                var downResult = ToRdpInputResult(
                    sink.SendKeyboardEvent(
                        KbdCodeType.Unicode,
                        codeUnit,
                        false,
                        false,
                        false),
                    "type text in the Enhanced Session");
                if (!downResult.Success)
                {
                    return downResult;
                }

                var upResult = ToRdpInputResult(
                    sink.SendKeyboardEvent(
                        KbdCodeType.Unicode,
                        codeUnit,
                        true,
                        false,
                        false),
                    "type text in the Enhanced Session");
                if (!upResult.Success)
                {
                    return upResult;
                }
            }

            return new(true, null);
        }

        private HyperVConsoleCommandResult SignIn(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName) || password.Length == 0)
            {
                return new(false, "No saved guest credentials are available.");
            }

            var typeResult = TypeText(password);
            if (!typeResult.Success)
            {
                return typeResult;
            }

            return PressKey("ENTER");
        }

        private HyperVConsoleCommandResult PressKey(string chord)
        {
            return _useEnhancedSession
                ? PressEnhancedKey(chord)
                : PressBasicKey(chord);
        }

        private HyperVConsoleCommandResult PressBasicKey(string chord)
        {
            var parts = chord
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static part => part.ToUpperInvariant())
                .ToArray();
            if (parts.Length == 0)
            {
                return new(false, "A key is required.");
            }

            var keyboard = GetKeyboard();
            if (parts is ["CTRL" or "CONTROL", "ALT", "DELETE" or "DEL"])
            {
                return ToCommandResult(
                    InvokeDeviceMethod(keyboard, "TypeCtrlAltDel"),
                    "send Ctrl+Alt+Delete to the VM");
            }

            var modifiers = new List<ushort>(4);
            foreach (var part in parts[..^1])
            {
                var modifier = part switch
                {
                    "CTRL" or "CONTROL" => VkControl,
                    "ALT" => VkMenu,
                    "SHIFT" => VkShift,
                    "WIN" or "WINDOWS" => VkLwin,
                    _ => (ushort)0,
                };
                if (modifier == 0)
                {
                    return new(false, $"Unknown modifier '{part}'.");
                }

                modifiers.Add(modifier);
            }

            if (!TryResolveVirtualKey(parts[^1], out var key))
            {
                return new(false, $"Unknown key '{parts[^1]}'.");
            }

            var pressedModifiers = new List<ushort>(modifiers.Count);
            try
            {
                foreach (var modifier in modifiers)
                {
                    var pressResult = ToCommandResult(
                        InvokeDeviceMethod(
                            keyboard,
                            "PressKey",
                            ("keyCode", (uint)modifier)),
                        "press a modifier in the VM");
                    if (!pressResult.Success)
                    {
                        return pressResult;
                    }

                    pressedModifiers.Add(modifier);
                }

                return ToCommandResult(
                    InvokeDeviceMethod(
                        keyboard,
                        "TypeKey",
                        ("keyCode", (uint)key)),
                    $"send {parts[^1]} to the VM");
            }
            finally
            {
                for (var index = pressedModifiers.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        _ = InvokeDeviceMethod(
                            keyboard,
                            "ReleaseKey",
                            ("keyCode", (uint)pressedModifiers[index]));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private HyperVConsoleCommandResult PressEnhancedKey(string chord)
        {
            var parts = chord
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static part => part.ToUpperInvariant())
                .ToArray();
            if (parts.Length == 0)
            {
                return new(false, "A key is required.");
            }

            var modifiers = new List<ushort>(4);
            foreach (var part in parts[..^1])
            {
                var modifier = part switch
                {
                    "CTRL" or "CONTROL" => VkControl,
                    "ALT" => VkMenu,
                    "SHIFT" => VkShift,
                    "WIN" or "WINDOWS" => VkLwin,
                    _ => (ushort)0,
                };
                if (modifier == 0)
                {
                    return new(false, $"Unknown modifier '{part}'.");
                }

                modifiers.Add(modifier);
            }

            if (!TryResolveVirtualKey(parts[^1], out var key))
            {
                return new(false, $"Unknown key '{parts[^1]}'.");
            }

            var pressedModifiers = new List<ushort>(modifiers.Count);
            try
            {
                foreach (var modifier in modifiers)
                {
                    var pressResult = SendEnhancedVirtualKey(modifier, keyUp: false);
                    if (!pressResult.Success)
                    {
                        return pressResult;
                    }

                    pressedModifiers.Add(modifier);
                }

                var downResult = SendEnhancedVirtualKey(key, keyUp: false);
                if (!downResult.Success)
                {
                    return downResult;
                }

                return SendEnhancedVirtualKey(key, keyUp: true);
            }
            finally
            {
                for (var index = pressedModifiers.Count - 1; index >= 0; index--)
                {
                    _ = SendEnhancedVirtualKey(pressedModifiers[index], keyUp: true);
                }
            }
        }

        private HyperVConsoleCommandResult SendEnhancedVirtualKey(ushort virtualKey, bool keyUp)
        {
            var mapped = MapVirtualKey(virtualKey, MapVkVkToVscEx);
            var scanCode = (ushort)(mapped & 0xFF);
            if (scanCode == 0)
            {
                return new(
                    false,
                    $"Windows could not map virtual key 0x{virtualKey:X2} to a scan code.");
            }

            var extended = (mapped & 0xFF00) != 0;
            return ToRdpInputResult(
                GetRdpInputSink().SendKeyboardEvent(
                    KbdCodeType.ScanCode,
                    scanCode,
                    keyUp,
                    false,
                    extended),
                $"send virtual key 0x{virtualKey:X2} to the Enhanced Session");
        }

        private HyperVConsoleCommandResult ScrollConsole(int x, int y, int delta)
        {
            return _useEnhancedSession
                ? ScrollEnhancedConsole(x, y, delta)
                : ScrollBasicConsole(x, y, delta);
        }

        private HyperVConsoleCommandResult ScrollBasicConsole(int x, int y, int delta)
        {
            if (!TryResolveGuestPoint(x, y, out var guestX, out var guestY, out var error))
            {
                return new(false, error);
            }

            var mouse = GetMouse();
            var moveResult = ToCommandResult(
                InvokeDeviceMethod(
                    mouse,
                    "SetAbsolutePosition",
                    ("horizontalPosition", guestX),
                    ("verticalPosition", guestY)),
                "move the VM pointer");
            if (!moveResult.Success)
            {
                return moveResult;
            }

            return ToCommandResult(
                InvokeDeviceMethod(
                    mouse,
                    "SetScrollPosition",
                    ("scrollPositionDelta", delta)),
                "scroll in the VM");
        }

        private HyperVConsoleCommandResult ScrollEnhancedConsole(int x, int y, int delta)
        {
            if (!TryResolveRdpPoint(x, y, out var guestX, out var guestY, out var error))
            {
                return new(false, error);
            }

            var sink = GetRdpInputSink();
            var moveResult = ToRdpInputResult(
                sink.SendMouseMoveEvent((uint)guestX, (uint)guestY),
                "move the pointer in the Enhanced Session");
            if (!moveResult.Success)
            {
                return moveResult;
            }

            return ToRdpInputResult(
                sink.SendMouseWheelEvent(unchecked((ushort)(short)delta)),
                "scroll in the Enhanced Session");
        }

        private bool TryResolveRdpPoint(
            int x,
            int y,
            out int guestX,
            out int guestY,
            out string? error)
        {
            var size = _activeX.ClientSize;
            if (x < 0 || y < 0 || x >= size.Width || y >= size.Height)
            {
                guestX = 0;
                guestY = 0;
                error = $"The point ({x}, {y}) is outside the {size.Width}×{size.Height} console.";
                return false;
            }

            guestX = Math.Clamp(
                (int)Math.Round(
                    x * (_sessionDisplaySize.Width - 1d) / Math.Max(1, size.Width - 1)),
                0,
                _sessionDisplaySize.Width - 1);
            guestY = Math.Clamp(
                (int)Math.Round(
                    y * (_sessionDisplaySize.Height - 1d) / Math.Max(1, size.Height - 1)),
                0,
                _sessionDisplaySize.Height - 1);
            error = null;
            return true;
        }

        /// <summary>
        /// Basic-session coordinates are IDENTITY: the framebuffer capture is exactly the
        /// guest display, so screenshot pixels are guest pixels and the only work here is a
        /// bounds check against the video head's native resolution. The previous version
        /// stretched control-relative coordinates onto the guest resolution — but the Basic
        /// console renders the guest 1:1 CENTERED in the control (SmartSizing only ever
        /// shrinks), so every click aimed at a screenshot landed offset onto the wallpaper
        /// with ok:true. Bounds deliberately use the GUEST resolution, not the control size:
        /// a panel smaller than the guest desktop must not reject valid coordinates.
        /// </summary>
        private bool TryResolveGuestPoint(
            int x,
            int y,
            out int guestX,
            out int guestY,
            out string? error)
        {
            var videoHead = GetVideoHead();
            var width = Convert.ToInt32(videoHead["CurrentHorizontalResolution"]);
            var height = Convert.ToInt32(videoHead["CurrentVerticalResolution"]);
            if (width <= 0 || height <= 0)
            {
                guestX = 0;
                guestY = 0;
                error = "Hyper-V did not report the VM display resolution.";
                return false;
            }

            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                guestX = 0;
                guestY = 0;
                error = $"The point ({x}, {y}) is outside the {width}×{height} guest display. "
                    + "Coordinates come from the latest hyperv_screenshot of this machine.";
                return false;
            }

            guestX = x;
            guestY = y;
            error = null;
            return true;
        }

        private static bool TryResolveVirtualKey(string name, out ushort key)
        {
            if (name.Length == 1)
            {
                var character = name[0];
                if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
                {
                    key = character;
                    return true;
                }
            }

            key = name switch
            {
                "CTRL" or "CONTROL" => VkControl,
                "ALT" => VkMenu,
                "SHIFT" => VkShift,
                "WIN" or "WINDOWS" => VkLwin,
                "ENTER" or "RETURN" => 0x0D,
                "TAB" => 0x09,
                "ESC" or "ESCAPE" => 0x1B,
                "SPACE" => 0x20,
                "BACKSPACE" => 0x08,
                "DELETE" or "DEL" => 0x2E,
                "INSERT" or "INS" => 0x2D,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" or "PGUP" => 0x21,
                "PAGEDOWN" or "PGDN" => 0x22,
                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,
                "F1" => 0x70,
                "F2" => 0x71,
                "F3" => 0x72,
                "F4" => 0x73,
                "F5" => 0x74,
                "F6" => 0x75,
                "F7" => 0x76,
                "F8" => 0x77,
                "F9" => 0x78,
                "F10" => 0x79,
                "F11" => 0x7A,
                "F12" => 0x7B,
                _ => 0,
            };
            return key != 0;
        }

        private IMsRdpInputSink GetRdpInputSink()
        {
            if (!_useEnhancedSession)
            {
                throw new InvalidOperationException(
                    "The RDP input sink is only available in Enhanced Session.");
            }

            try
            {
                return _rdpInputSink ??=
                    (IMsRdpInputSink)_activeX.ActiveXInstance;
            }
            catch (InvalidCastException ex)
            {
                throw new InvalidOperationException(
                    "This Windows Remote Desktop control does not expose Enhanced Session input.",
                    ex);
            }
        }

        private ManagementObject GetVirtualMachine()
        {
            if (_virtualMachine is not null)
            {
                return _virtualMachine;
            }

            var localHost = string.Equals(
                    _hostName,
                    Environment.MachineName,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(_hostName, "localhost", StringComparison.OrdinalIgnoreCase)
                || _hostName == ".";
            var scope = new ManagementScope(
                localHost
                    ? @"root\virtualization\v2"
                    : $@"\\{_hostName}\root\virtualization\v2");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{_machineId:D}'"));
            using var results = searcher.Get();
            foreach (ManagementObject machine in results)
            {
                _virtualMachine = machine;
                return machine;
            }

            throw new InvalidOperationException(
                $"Hyper-V could not find virtual machine {_machineId:D}.");
        }

        private ManagementObject GetKeyboard() =>
            _keyboard ??= GetFirstRelated(
                GetVirtualMachine(),
                "Msvm_Keyboard",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

        private ManagementObject GetMouse() =>
            _mouse ??= GetFirstRelated(
                GetVirtualMachine(),
                "Msvm_SyntheticMouse",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

        /// <summary>
        /// The host's one <c>Msvm_VirtualSystemManagementService</c> in the same
        /// virtualization scope as the machine — the object that owns
        /// <c>GetVirtualSystemThumbnailImage</c>.
        /// </summary>
        private ManagementObject GetManagementService()
        {
            if (_managementService is not null)
            {
                return _managementService;
            }

            using var searcher = new ManagementObjectSearcher(
                GetVirtualMachine().Scope,
                new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
            using var results = searcher.Get();
            foreach (ManagementObject service in results)
            {
                _managementService = service;
                return service;
            }

            throw new InvalidOperationException(
                "Hyper-V did not expose its virtual system management service.");
        }

        /// <summary>
        /// The machine's ACTIVE <c>Msvm_VirtualSystemSettingData</c> (its current, realized
        /// settings — reached via <c>Msvm_SettingsDefineState</c>, which only the running
        /// state has). The framebuffer API addresses the VM by this reference.
        /// </summary>
        private ManagementObject GetActiveSettingData() =>
            _activeSettingData ??= GetFirstRelated(
                GetVirtualMachine(),
                "Msvm_VirtualSystemSettingData",
                "Msvm_SettingsDefineState",
                "SettingData",
                "ManagedElement");

        private ManagementObject GetVideoHead()
        {
            if (_videoHead is not null)
            {
                // The cached instance is a client-side property snapshot; the guest's
                // resolution changes across its life (boot VGA -> sign-in -> native), and
                // both the framebuffer capture size and the identity click bounds read it.
                try
                {
                    _videoHead.Get();
                    return _videoHead;
                }
                catch (Exception ex) when (
                    ex is ManagementException
                        or COMException
                        or UnauthorizedAccessException)
                {
                    // Every way a cached instance can go dead ends here, not just the WMI
                    // one: a reset VM re-enumerates its display controller, and the RPC
                    // layer reports that as a COM failure. Dropping the object and
                    // re-fetching below is the recovery; letting it escape would strand
                    // every later capture and click on this console.
                    _videoHead.Dispose();
                    _videoHead = null;
                }
            }

            using var controller = GetFirstRelated(
                GetVirtualMachine(),
                "Msvm_SyntheticDisplayController",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");
            _videoHead = GetFirstRelated(
                controller,
                "Msvm_VideoHead",
                "Msvm_VideoHeadOnController",
                "Dependent",
                "Antecedent");
            return _videoHead;
        }

        private static ManagementObject GetFirstRelated(
            ManagementObject source,
            string resultClass,
            string associationClass,
            string resultRole,
            string role)
        {
            using var related = source.GetRelated(
                resultClass,
                associationClass,
                null,
                null,
                resultRole,
                role,
                false,
                null);
            foreach (ManagementObject item in related)
            {
                return item;
            }

            throw new InvalidOperationException(
                $"Hyper-V did not expose {resultClass} for this virtual machine.");
        }

        private static uint InvokeDeviceMethod(
            ManagementObject device,
            string methodName,
            params (string Name, object Value)[] parameters)
        {
            using var input = parameters.Length == 0
                ? null
                : device.GetMethodParameters(methodName);
            if (input is not null)
            {
                foreach (var parameter in parameters)
                {
                    input[parameter.Name] = parameter.Value;
                }
            }

            using var output = device.InvokeMethod(methodName, input, null)
                ?? throw new InvalidOperationException(
                    $"Hyper-V returned no result for {methodName}.");
            return Convert.ToUInt32(output["ReturnValue"]);
        }

        private static HyperVConsoleCommandResult ToCommandResult(
            uint returnCode,
            string action)
        {
            if (returnCode == 0)
            {
                return new(true, null);
            }

            var reason = returnCode switch
            {
                1 => "the text contains a character the VM keyboard cannot translate",
                32769 => "Hyper-V denied access to the VM input device",
                32770 => "the VM input device does not support this operation",
                32773 => "Hyper-V rejected the input parameters",
                32775 => "the VM is not in a state that accepts input",
                32777 => "the VM input device is unavailable",
                _ => $"Hyper-V returned error {returnCode}",
            };
            return new(false, $"Could not {action}: {reason}.");
        }

        private static HyperVConsoleCommandResult ToRdpInputResult(
            int hresult,
            string action)
        {
            if (hresult >= 0)
            {
                return new(true, null);
            }

            var reason = Marshal.GetExceptionForHR(hresult)?.Message;
            return new(
                false,
                string.IsNullOrWhiteSpace(reason)
                    ? $"Could not {action} (HRESULT 0x{hresult:X8})."
                    : $"Could not {action}: {FirstLine(reason)}");
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_parentWindow != 0 && !IsWindow(_parentWindow))
            {
                Complete(false, "The Machine panel was closed.");
                Dispose();
                return;
            }

            ResizeToParent();
            if (ReadConnectionState() == 1)
            {
                Complete(true, null);
                return;
            }

            if (DateTimeOffset.UtcNow >= _connectionDeadline)
            {
                // Name the fallback here and nowhere else. A Basic console that also fails to
                // connect is the one moment where "why am I not in Enhanced Session?" and "why
                // is there no picture?" look like the same problem, and they usually are not.
                Complete(
                    false,
                    _useEnhancedSession
                        ? "Enhanced Session did not connect within 30 seconds."
                        : _enhancedFallbackReason is { Length: > 0 } fallback
                            ? $"The Hyper-V console did not connect within 30 seconds. "
                                + $"Enhanced Session was not used: {fallback}"
                            : "The Hyper-V console did not connect within 30 seconds.");
                Dispose();
            }
        }

        private void OnRdpConnected() => Complete(true, null);

        private void OnRdpDisconnected(int reason)
        {
            if (_connection?.Task.IsCompleted != false)
            {
                return;
            }

            Complete(false, DescribeDisconnect(reason));
            Dispose();
        }

        private void OnRdpFatalError(int errorCode)
        {
            if (_connection?.Task.IsCompleted != false)
            {
                return;
            }

            Complete(
                false,
                $"The Remote Desktop control could not start the Hyper-V session "
                + $"(error {errorCode}).");
            Dispose();
        }

        private void OnRdpLogonError(int errorCode)
        {
            if (_connection?.Task.IsCompleted != false)
            {
                return;
            }

            Complete(
                false,
                errorCode is 0 or -1
                    ? "Windows could not open the Enhanced Session sign-in prompt."
                    : $"Enhanced Session sign-in failed (RDP logon error {errorCode}).");
            Dispose();
        }

        private string DescribeDisconnect(int reason)
        {
            if (_useEnhancedSession && reason == CredentialFailureDisconnectReason)
            {
                return "Windows could not authenticate the Hyper-V console connection.";
            }

            try
            {
                dynamic client = _activeX.ActiveXInstance;
                var extendedReason = Convert.ToUInt32(client.ExtendedDisconnectReason);
                var description = (string?)client.GetErrorDescription(
                    Convert.ToUInt32(reason),
                    extendedReason);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return FirstLine(description);
                }
            }
            catch (Exception)
            {
            }

            return $"The Hyper-V console disconnected before sign-in (reason {reason}).";
        }

        private void ResizeToParent()
        {
            var parentSize = ReadParentClientSize();
            if (_parentWindow != 0) SetWindowPos(
                Handle,
                nint.Zero,
                0,
                0,
                parentSize.Width,
                parentSize.Height,
                SwpNoActivate | SwpShowWindow);

            var displaySize = NormalizeSessionDisplaySize(parentSize);
            var displayScaleFactor = ResolveDesktopScaleFactor();
            if (!_useEnhancedSession
                || _dynamicResolutionUnavailable
                || ReadConnectionState() != 1
                || (displaySize == _sessionDisplaySize
                    && displayScaleFactor == _sessionDisplayScaleFactor))
            {
                return;
            }

            try
            {
                var dpi = Math.Max(96u, GetDpiForWindow(_parentWindow));
                var physicalWidth = Math.Max(
                    1u,
                    (uint)Math.Round(displaySize.Width * 25.4 / dpi));
                var physicalHeight = Math.Max(
                    1u,
                    (uint)Math.Round(displaySize.Height * 25.4 / dpi));
                var client =
                    (IMsRdpClient9Dispatch)_activeX.ActiveXInstance;
                client.UpdateSessionDisplaySettings(
                    (uint)displaySize.Width,
                    (uint)displaySize.Height,
                    physicalWidth,
                    physicalHeight,
                    0u,
                    displayScaleFactor,
                    ResolveDeviceScaleFactor(displayScaleFactor));
                _sessionDisplaySize = displaySize;
                _sessionDisplayScaleFactor = displayScaleFactor;
            }
            catch (Exception ex) when (
                ex is COMException
                    or InvalidCastException)
            {
                _dynamicResolutionUnavailable = true;
            }
        }

        private Size ReadParentClientSize()
        {
            if (_parentWindow == 0) return ClientSize;
            if (!GetClientRect(_parentWindow, out var rect))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new(
                Math.Max(1, rect.Right - rect.Left),
                Math.Max(1, rect.Bottom - rect.Top));
        }

        private static Size NormalizeSessionDisplaySize(Size size) =>
            new(
                Math.Clamp(size.Width, 200, 8192),
                Math.Clamp(size.Height, 200, 8192));

        private uint ResolveDesktopScaleFactor()
        {
            if (_requestedDisplayScalePercent != 0)
            {
                return (uint)_requestedDisplayScalePercent;
            }

            var hostScale = Math.Max(96u, GetDpiForWindow(_parentWindow == 0 ? Handle : _parentWindow)) * 100d / 96d;
            var nearest = SupportedDesktopScaleFactors[0];
            var nearestDistance = Math.Abs(hostScale - nearest);
            foreach (var scale in SupportedDesktopScaleFactors[1..])
            {
                var distance = Math.Abs(hostScale - scale);
                if (distance < nearestDistance)
                {
                    nearest = scale;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static uint ResolveDeviceScaleFactor(uint desktopScaleFactor) =>
            desktopScaleFactor switch
            {
                < 125u => 100u,
                < 175u => 140u,
                _ => 180u,
            };

        private short ReadConnectionState()
        {
            try
            {
                dynamic client = _activeX.ActiveXInstance;
                return (short)client.Connected;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void Complete(bool success, string? error)
        {
            // The app requested a mode but the broker may have silently fallen back to
            // Basic; the result carries what actually connected, because capture semantics
            // (headless framebuffer vs real-screen copy) differ between the two — and, on a
            // connect that succeeded anyway, why Enhanced was not used.
            var note = success && _enhancedFallbackReason is { Length: > 0 } fallback
                ? $"Enhanced Session was not used: {fallback}"
                : null;
            if (_connection?.TrySetResult(new(success, error, _useEnhancedSession, note)) == true)
            {
                if (success)
                {
                    _connectionDeadline = DateTimeOffset.MaxValue;
                }
                else
                {
                    _timer.Stop();
                }
            }
        }

        private sealed class RemoteDesktopAxHost : AxHost
        {
            private readonly RemoteDesktopEventSink _eventSink;
            private ConnectionPointCookie? _connectionPoint;

            public RemoteDesktopAxHost()
                : base("A0C63C30-F08D-4AB4-907C-34905D770C7D")
            {
                _eventSink = new(this);
            }

            public event Action? Connected;

            public event Action<int>? Disconnected;

            public event Action<int>? FatalError;

            public event Action<int>? LogonError;

            public object ActiveXInstance =>
                GetOcx()
                ?? throw new InvalidOperationException(
                    "The Remote Desktop control is not initialized.");

            protected override void CreateSink()
            {
                base.CreateSink();
                _connectionPoint = new(
                    this,
                    _eventSink,
                    typeof(IMsTscAxEvents));
            }

            protected override void DetachSink()
            {
                _connectionPoint?.Disconnect();
                _connectionPoint = null;
                base.DetachSink();
            }

            private sealed class RemoteDesktopEventSink(RemoteDesktopAxHost owner)
                : StandardOleMarshalObject, IMsTscAxEvents
            {
                public void OnConnected() => owner.Connected?.Invoke();

                public void OnDisconnected(int reason) =>
                    owner.Disconnected?.Invoke(reason);

                public void OnFatalError(int errorCode) =>
                    owner.FatalError?.Invoke(errorCode);

                public void OnLogonError(int errorCode) =>
                    owner.LogonError?.Invoke(errorCode);
            }
        }

        [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IMsTscAxEvents
        {
            [DispId(2)]
            void OnConnected();

            [DispId(4)]
            void OnDisconnected(int discReason);

            [DispId(10)]
            void OnFatalError(int errorCode);

            [DispId(22)]
            void OnLogonError(int errorCode);
        }

        [ComImport]
        [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMsRdpExtendedSettings
        {
            void set_Property(
                [MarshalAs(UnmanagedType.BStr)] string propertyName,
                [In, MarshalAs(UnmanagedType.Struct)] ref object value);

            [return: MarshalAs(UnmanagedType.Struct)]
            object get_Property(
                [MarshalAs(UnmanagedType.BStr)] string propertyName);
        }

        [ComImport]
        [Guid("28904001-04B6-436C-A55B-0AF1A0883DC9")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IMsRdpClient9Dispatch
        {
            [DispId(802)]
            void UpdateSessionDisplaySettings(
                uint desktopWidth,
                uint desktopHeight,
                uint physicalWidth,
                uint physicalHeight,
                uint orientation,
                uint desktopScaleFactor,
                uint deviceScaleFactor);
        }

        private enum MouseButtonType
        {
            Button1 = 0,
            Button2 = 1,
            Button3 = 2,
            XButton1 = 3,
            XButton2 = 4,
            XButton3 = 5,
        }

        private enum KbdCodeType
        {
            ScanCode = 0,
            Unicode = 1,
        }

        [ComImport]
        [Guid("4606850E-76A7-4E28-A47E-C7174F619351")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMsRdpInputSink
        {
            [PreserveSig]
            int SendMouseButtonEvent(
                MouseButtonType buttonType,
                [MarshalAs(UnmanagedType.VariantBool)] bool buttonDown,
                uint x,
                uint y);

            [PreserveSig]
            int SendMouseMoveEvent(uint x, uint y);

            [PreserveSig]
            int SendMouseWheelEvent(ushort wheelRotation);

            [PreserveSig]
            int SendKeyboardEvent(
                KbdCodeType codeType,
                ushort keyCode,
                [MarshalAs(UnmanagedType.VariantBool)] bool keyUp,
                [MarshalAs(UnmanagedType.VariantBool)] bool repeat,
                [MarshalAs(UnmanagedType.VariantBool)] bool extended);

            [PreserveSig]
            int SendSyncEvent(uint syncFlags);

            [PreserveSig]
            int BeginTouchFrame();

            [PreserveSig]
            int AddTouchInput(uint contactId, uint inputEvent, int x, int y);

            [PreserveSig]
            int EndTouchFrame();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetParent(nint child, nint newParent);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint window);

        private delegate bool EnumWindowsCallback(nint window, nint data);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint window);

        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint errorCode);
    }
}
