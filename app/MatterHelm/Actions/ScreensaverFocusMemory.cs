using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>Native foreground-window boundary used by <see cref="ScreensaverFocusMemory"/>.</summary>
internal interface IForegroundWindowNative
{
    nint GetForegroundWindow();

    bool IsWindow(nint window);

    uint GetWindowProcessId(nint window);

    string? GetProcessName(uint processId);

    string GetWindowTitle(nint window);

    bool IsMinimized(nint window);

    void RestoreWindow(nint window);

    bool SetForegroundWindow(nint window);

    uint GetWindowThreadId(nint window);

    uint GetCurrentThreadId();

    bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);
}

/// <summary>
/// Remembers the foreground window displaced by a MatterHelm-started
/// screensaver and validates its identity before one best-effort restore.
/// State is process-local and deliberately never persisted.
/// </summary>
internal sealed partial class ScreensaverFocusMemory
{
    private readonly Lock _gate = new();
    private readonly IForegroundWindowNative _native;
    private readonly Action<string, string> _log;
    private CapturedWindow? _capture;

    internal ScreensaverFocusMemory(
        IForegroundWindowNative? native = null,
        Action<string, string>? log = null)
    {
        _native = native ?? new WindowsForegroundWindowNative();
        _log = log ?? WriteLog;
    }

    internal bool HasCapture
    {
        get
        {
            lock (_gate)
            {
                return _capture is not null;
            }
        }
    }

    /// <summary>Replaces any prior capture with the current foreground window.</summary>
    internal bool Capture()
    {
        lock (_gate)
        {
            _capture = null;
            nint window = _native.GetForegroundWindow();
            if (window == 0)
            {
                _log("WARN", "screensaver focus: capture skipped because Windows reported no foreground window.");
                return false;
            }

            uint processId = _native.GetWindowProcessId(window);
            if (processId == 0)
            {
                _log("WARN", $"screensaver focus: capture skipped because the foreground HWND {FormatHandle(window)} had no owning process.");
                return false;
            }

            string? processName = _native.GetProcessName(processId);
            if (string.IsNullOrWhiteSpace(processName))
            {
                _log("WARN", $"screensaver focus: capture skipped because process {processId} could not be identified.");
                return false;
            }

            string title = NormalizeTitle(_native.GetWindowTitle(window));
            _capture = new CapturedWindow(window, processId, processName, title);
            _log("INFO", $"screensaver focus: captured {Describe(_capture)}.");
            return true;
        }
    }

    /// <summary>
    /// Validates and consumes the capture, restoring a minimized target before
    /// foreground activation. No prior capture is a successful no-op.
    /// </summary>
    internal bool Restore()
    {
        CapturedWindow? captured;
        lock (_gate)
        {
            captured = _capture;
            _capture = null;
        }

        if (captured is null)
        {
            _log("INFO", "screensaver focus: no captured foreground window to restore.");
            return true;
        }

        if (!_native.IsWindow(captured.Window))
        {
            return Refuse(captured, "the captured HWND is no longer a window");
        }

        uint currentProcessId = _native.GetWindowProcessId(captured.Window);
        if (currentProcessId != captured.ProcessId)
        {
            return Refuse(
                captured,
                $"the HWND now belongs to process {currentProcessId} instead of {captured.ProcessId}");
        }

        string? currentProcessName = _native.GetProcessName(currentProcessId);
        if (!string.Equals(currentProcessName, captured.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                captured,
                $"process identity changed from '{captured.ProcessName}' to '{currentProcessName ?? "unresolved"}'");
        }

        if (_native.IsMinimized(captured.Window))
        {
            _native.RestoreWindow(captured.Window);
        }

        if (_native.SetForegroundWindow(captured.Window))
        {
            _log("INFO", $"screensaver focus: restored {Describe(captured)} with SetForegroundWindow.");
            return true;
        }

        return RestoreWithAttachedInput(captured);
    }

    /// <summary>Invalidates any capture at a bridge/app lifecycle boundary.</summary>
    internal void Clear(string reason)
    {
        CapturedWindow? cleared;
        lock (_gate)
        {
            cleared = _capture;
            _capture = null;
        }

        if (cleared is not null)
        {
            _log("INFO", $"screensaver focus: cleared {Describe(cleared)} because {reason}.");
        }
    }

    private bool RestoreWithAttachedInput(CapturedWindow captured)
    {
        nint foreground = _native.GetForegroundWindow();
        if (foreground == 0)
        {
            return Refuse(captured, "SetForegroundWindow was refused and no current foreground input queue exists for fallback");
        }

        uint foregroundThreadId = _native.GetWindowThreadId(foreground);
        uint currentThreadId = _native.GetCurrentThreadId();
        if (foregroundThreadId == 0 || currentThreadId == 0)
        {
            return Refuse(captured, "SetForegroundWindow was refused and a fallback thread id could not be resolved");
        }

        if (foregroundThreadId == currentThreadId)
        {
            return _native.SetForegroundWindow(captured.Window)
                ? RestoreSucceeded(captured, "SetForegroundWindow retry on the foreground input thread")
                : Refuse(captured, "Windows refused SetForegroundWindow on both activation attempts");
        }

        if (!_native.AttachThreadInput(currentThreadId, foregroundThreadId, attach: true))
        {
            return Refuse(captured, "SetForegroundWindow was refused and AttachThreadInput could not attach to the current foreground thread");
        }

        bool activated;
        try
        {
            activated = _native.SetForegroundWindow(captured.Window);
        }
        finally
        {
            if (!_native.AttachThreadInput(currentThreadId, foregroundThreadId, attach: false))
            {
                _log("WARN", $"screensaver focus: input queues did not detach cleanly after targeting {Describe(captured)}.");
            }
        }

        return activated
            ? RestoreSucceeded(captured, "AttachThreadInput fallback")
            : Refuse(captured, "Windows refused SetForegroundWindow after the AttachThreadInput fallback");
    }

    private bool RestoreSucceeded(CapturedWindow captured, string route)
    {
        _log("INFO", $"screensaver focus: restored {Describe(captured)} with {route}.");
        return true;
    }

    private bool Refuse(CapturedWindow captured, string reason)
    {
        _log("WARN", $"screensaver focus: did not restore {Describe(captured)} because {reason}.");
        return false;
    }

    private static string Describe(CapturedWindow captured) =>
        $"'{captured.Title}' ({captured.ProcessName}, pid {captured.ProcessId}, HWND {FormatHandle(captured.Window)})";

    private static string FormatHandle(nint window) => $"0x{window:X}";

    private static string NormalizeTitle(string title)
    {
        string normalized = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length == 0 ? "<untitled>" : normalized;
    }

    private static void WriteLog(string level, string message)
    {
        if (level == "INFO")
        {
            Log.Info(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    private sealed record CapturedWindow(nint Window, uint ProcessId, string ProcessName, string Title);

    private sealed partial class WindowsForegroundWindowNative : IForegroundWindowNative
    {
        private const int SwRestore = 9;

        public nint GetForegroundWindow() => GetForegroundWindowNative();

        public bool IsWindow(nint window) => IsWindowNative(window);

        public uint GetWindowProcessId(nint window)
        {
            _ = GetWindowThreadProcessId(window, out uint processId);
            return processId;
        }

        public string? GetProcessName(uint processId)
        {
            try
            {
                using Process process = Process.GetProcessById(checked((int)processId));
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or OverflowException
                or Win32Exception)
            {
                return null;
            }
        }

        public string GetWindowTitle(nint window)
        {
            int length = GetWindowTextLengthW(window);
            if (length <= 0)
            {
                return "";
            }

            nint buffer = Marshal.AllocHGlobal(checked((length + 1) * sizeof(char)));
            try
            {
                int copied = GetWindowTextW(window, buffer, length + 1);
                return copied > 0 ? Marshal.PtrToStringUni(buffer, copied) : "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool IsMinimized(nint window) => IsIconic(window);

        public void RestoreWindow(nint window) => _ = ShowWindow(window, SwRestore);

        public bool SetForegroundWindow(nint window) => SetForegroundWindowNative(window);

        public uint GetWindowThreadId(nint window) => GetWindowThreadProcessId(window, out _);

        public uint GetCurrentThreadId() => GetCurrentThreadIdNative();

        public bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach) =>
            AttachThreadInputNative(attachThreadId, attachToThreadId, attach);

        [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static partial nint GetForegroundWindowNative();

        [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowNative(nint window);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

        [LibraryImport("user32.dll")]
        private static partial int GetWindowTextLengthW(nint window);

        [LibraryImport("user32.dll")]
        private static partial int GetWindowTextW(nint window, nint text, int maxCount);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(nint window, int command);

        [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindowNative(nint window);

        [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static partial uint GetCurrentThreadIdNative();

        [LibraryImport("user32.dll", EntryPoint = "AttachThreadInput")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AttachThreadInputNative(
            uint attachThreadId,
            uint attachToThreadId,
            [MarshalAs(UnmanagedType.Bool)] bool attach);
    }
}
