using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>
/// Display and power control. Uses DDC/CI hardware power where supported, so
/// Windows never sees a display-off transition that can trigger Modern Standby.
/// If no physical monitor accepts DDC/CI, falls back to <c>SC_MONITORPOWER 2</c>
/// on a private message-only window (ADR-003: never <c>HWND_BROADCAST</c>) plus
/// the legacy keep-awake hold. Wake includes a net-zero mouse nudge because
/// <c>SC_MONITORPOWER -1</c> is unreliable since Windows 8.
/// </summary>
public sealed partial class DisplayPower : IDisposable
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private static readonly nint HwndMessage = -3;

    private readonly Lock _gate = new();
    private readonly MessageOnlyWindow? _window;
    private readonly IDdcDisplayPower _ddcDisplayPower;
    private readonly DisplayAwakeGuard _awakeGuard;
    private readonly Func<bool> _blankDisplays;
    private readonly Func<bool> _wakeDisplays;
    private readonly Action<string, string> _log;

    private IReadOnlyList<DdcMonitor> _ddcManagedMonitors = [];
    private IReadOnlyList<DdcMonitorPowerResult> _lastOffResults = [];
    private bool _usedBlankingFallback;

    /// <summary>Creates the production display-power controller.</summary>
    public DisplayPower()
    {
        _window = new MessageOnlyWindow();
        _ddcDisplayPower = new DdcDisplayPower();
        _awakeGuard = new DisplayAwakeGuard(SetThreadExecutionState, DefaultLog);
        _blankDisplays = () =>
        {
            // SendMessage's return value carries no success signal for SC_MONITORPOWER.
            _ = SendMessageW(_window.Handle, WmSysCommand, ScMonitorPower, MonitorOff);
            return true;
        };
        _wakeDisplays = WakeDisplays;
        _log = DefaultLog;
    }

    internal DisplayPower(
        IDdcDisplayPower ddcDisplayPower,
        DisplayAwakeGuard awakeGuard,
        Func<bool> blankDisplays,
        Func<bool> wakeDisplays,
        Action<string, string> log)
    {
        _ddcDisplayPower = ddcDisplayPower;
        _awakeGuard = awakeGuard;
        _blankDisplays = blankDisplays;
        _wakeDisplays = wakeDisplays;
        _log = log;
    }

    /// <summary>Puts all displays into their low-power (off) state.</summary>
    public bool DisplaysOff()
    {
        lock (_gate)
        {
            IReadOnlyList<DdcMonitorPowerResult> results = _ddcDisplayPower.SetPower(DdcPowerMode.Off);
            DdcMonitor[] managed = [.. results.Where(result => result.Success).Select(result => result.Monitor)];
            _lastOffResults = results;
            _ddcManagedMonitors = managed;

            if (managed.Length > 0)
            {
                _usedBlankingFallback = false;
                LogOffTransition(results, usedFallback: false, holdAcquired: false);
                if (results.Any(result => !result.Success))
                {
                    _log(
                        "WARN",
                        "Display power: mixed DDC/CI support; unsupported displays were left on because global blanking can trigger Modern Standby.");
                }

                return true;
            }

            // SC_MONITORPOWER is global, so use it only when DDC/CI managed no
            // display at all. In a mixed setup it would throw away the hardware
            // path's core benefit by presenting screen-off to Windows.
            bool holdAcquired = _awakeGuard.Acquire();
            if (!holdAcquired)
            {
                _usedBlankingFallback = false;
                LogOffTransition(results, usedFallback: false, holdAcquired: false);
                return false;
            }

            bool blanked = _blankDisplays();
            _usedBlankingFallback = blanked;
            LogOffTransition(results, usedFallback: blanked, holdAcquired: true);
            return blanked;
        }
    }

    /// <summary>Wakes displays, then releases the Modern Standby keep-awake hold.</summary>
    public bool DisplaysOn()
    {
        lock (_gate)
        {
            IReadOnlyList<DdcMonitorPowerResult> results = _ddcDisplayPower.SetPower(
                DdcPowerMode.On,
                _ddcManagedMonitors);
            bool ddcRestored = results.All(result => result.Success);
            bool woke = _wakeDisplays();
            bool released = _awakeGuard.Release();
            LogOnTransition(results, woke, released);
            _ddcManagedMonitors = [];
            _lastOffResults = [];
            _usedBlankingFallback = false;
            return ddcRestored & woke & released;
        }
    }

    /// <summary>Releases the keep-awake hold without waking displays (bridge/app teardown).</summary>
    public bool ReleaseKeepAwake() => _awakeGuard.Release();

    /// <summary>Wakes displays with a +1/-1 relative mouse nudge (cursor ends where it started).</summary>
    public static bool WakeDisplays()
    {
        var inputs = new NativeInput.Input[2];
        inputs[0].Type = NativeInput.InputMouse;
        inputs[0].Union.Mouse.Dx = 1;
        inputs[0].Union.Mouse.Dy = 1;
        inputs[0].Union.Mouse.Flags = NativeInput.MouseEventFMove;
        inputs[1] = inputs[0];
        inputs[1].Union.Mouse.Dx = -1;
        inputs[1].Union.Mouse.Dy = -1;
        return NativeInput.Send(inputs, "wake-displays mouse nudge");
    }

    /// <summary>Suspends the machine (sleep). Returns false if the system rejected the request.</summary>
    public static bool Sleep()
    {
        bool ok = Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false);
        if (!ok)
        {
            Log.Error("SetSuspendState(Suspend) was rejected by the system.");
        }

        return ok;
    }

    /// <summary>Clears the keep-awake hold and destroys the message-only window.</summary>
    public void Dispose()
    {
        _awakeGuard.Dispose();
        _window?.DestroyHandle();
    }

    private void LogOffTransition(
        IReadOnlyList<DdcMonitorPowerResult> results,
        bool usedFallback,
        bool holdAcquired)
    {
        string paths;
        if (results.Count == 0)
        {
            paths = "no physical DDC/CI monitors found";
        }
        else
        {
            bool mixed = results.Any(result => result.Success);
            paths = string.Join(
                "; ",
                results.Select(result => result.Success
                    ? $"{result.Monitor.Name}=DDC/CI VCP 0xD6→0x04"
                    : mixed
                        ? $"{result.Monitor.Name}=left on (DDC/CI failed: {result.Error})"
                        : usedFallback
                            ? $"{result.Monitor.Name}=SC_MONITORPOWER fallback (DDC/CI failed: {result.Error})"
                            : $"{result.Monitor.Name}=DDC/CI failed ({result.Error})"));
        }

        string fallback = usedFallback
            ? "; all displays=SC_MONITORPOWER fallback; keep-awake acquired; Modern Standby may still engage"
            : holdAcquired
                ? "; blanking fallback failed after keep-awake acquisition"
                : string.Empty;
        _log("INFO", $"Display power OFF: {paths}{fallback}.");
    }

    private void LogOnTransition(
        IReadOnlyList<DdcMonitorPowerResult> results,
        bool woke,
        bool released)
    {
        var paths = new List<string>();
        paths.AddRange(results.Select(result => result.Success
            ? $"{result.Monitor.Name}=DDC/CI VCP 0xD6→0x01"
            : $"{result.Monitor.Name}=DDC/CI restore failed ({result.Error})"));
        if (_lastOffResults.Any(result => !result.Success) && !_usedBlankingFallback)
        {
            paths.Add("non-DDC displays=already on (mixed policy)");
        }

        if (_usedBlankingFallback)
        {
            paths.Add($"SC_MONITORPOWER fallback=mouse nudge {(woke ? "sent" : "failed")}");
        }
        else
        {
            paths.Add($"wake nudge={(woke ? "sent" : "failed")}");
        }

        paths.Add($"keep-awake release={(released ? "complete" : "failed")}");
        _log("INFO", $"Display power ON: {string.Join("; ", paths)}.");
    }

    private static void DefaultLog(string level, string message)
    {
        if (level == "ERROR")
        {
            Log.Error(message);
        }
        else if (level == "WARN")
        {
            Log.Warn(message);
        }
        else
        {
            Log.Info(message);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);

    /// <summary>Invisible message-only window; exists only as a SendMessage target.</summary>
    private sealed class MessageOnlyWindow : NativeWindow
    {
        public MessageOnlyWindow() => CreateHandle(new CreateParams { Parent = HwndMessage });
    }
}
