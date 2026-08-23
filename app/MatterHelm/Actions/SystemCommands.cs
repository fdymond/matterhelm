using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MatterHelm.Actions;

/// <summary>
/// Executes the S8-5 <c>system</c> custom-action commands that are not
/// already executor verbs: screensaver start (launches the user's configured
/// .scr with the documented <c>/s</c> switch — deterministic and diagnosable,
/// unlike hoping <c>SC_SCREENSAVE</c> reaches the right window), workstation
/// lock, a graceful <c>WM_CLOSE</c> to the foreground window, and
/// shutdown/restart via <c>shutdown.exe</c> (runs unelevated for the
/// interactive user). Never throws; <c>false</c> = failure (logged), matching
/// the executor contract.
/// </summary>
public static partial class SystemCommands
{
    private const int WmClose = 0x0010;

    /// <summary>
    /// Starts the user's configured screensaver (HKCU <c>Control Panel\Desktop</c>,
    /// <c>SCRNSAVE.EXE</c>). No screensaver configured = failure, so the ack
    /// tells the user why nothing happened.
    /// </summary>
    public static bool StartScreenSaver()
    {
        string? configured;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            configured = key?.GetValue("SCRNSAVE.EXE") as string;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            Log.Error("startScreenSaver: no screensaver is configured in Windows (SCRNSAVE.EXE unset).");
            return false;
        }

        string path = Environment.ExpandEnvironmentVariables(configured);
        if (!Path.IsPathRooted(path))
        {
            // A bare "Bubbles.scr" style value resolves against System32.
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), path);
        }

        if (!File.Exists(path))
        {
            Log.Error($"startScreenSaver: configured screensaver not found: {path}");
            return false;
        }

        try
        {
            // "/s" is the documented run-now screensaver switch; detached like
            // AppLaunch (the .scr owns its own lifetime).
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "/s" },
                UseShellExecute = false,
            });
            if (process is null)
            {
                Log.Error($"startScreenSaver: {path} did not start.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"startScreenSaver: failed to start {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops a running screensaver by closing its <c>.scr</c> process (S9-8).
    /// Why not input injection: the original net-zero ±1 px mouse nudge sat
    /// below the screensaver framework's anti-jitter dismissal threshold and
    /// did nothing (owner report). Why not <c>SPI_GETSCREENSAVERRUNNING</c>:
    /// it only reports Windows-initiated (idle-timeout) savers — a saver
    /// launched by <see cref="StartScreenSaver"/> runs as a plain process it
    /// never sees. Both cases run the <c>.scr</c> as a user-session process,
    /// so enumerate-and-close covers them uniformly (live-probed: found +
    /// gone in under a second). No screensaver found = success, logged.
    /// </summary>
    public static bool StopScreenSaver()
    {
        bool found = false;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (path is null || !path.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                if (!process.CloseMainWindow())
                {
                    // No main window to close (preview-mode oddity) — a
                    // screensaver holds no user data, so kill is safe.
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Protected/exited/other-session processes: not ours to touch.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (!found)
        {
            Log.Info("stopScreenSaver: no screensaver process was running.");
        }

        return true;
    }

    /// <summary>Locks the workstation (same as Win+L).</summary>
    public static bool LockWorkstation()
    {
        if (!LockWorkStation())
        {
            Log.Error("lock: LockWorkStation was rejected by the system.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Asks the foreground window's program to exit by posting <c>WM_CLOSE</c> —
    /// the same graceful close as the title-bar X (apps may prompt to save,
    /// never a kill). No foreground window (e.g. the desktop) = failure.
    /// </summary>
    public static bool CloseForegroundProgram()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            Log.Error("closeForeground: no foreground window to close.");
            return false;
        }

        if (!PostMessageW(window, WmClose, 0, 0))
        {
            Log.Error("closeForeground: posting WM_CLOSE to the foreground window failed.");
            return false;
        }

        return true;
    }

    /// <summary>Shuts the machine down now (<c>shutdown.exe /s /t 0</c>).</summary>
    public static bool Shutdown() => RunShutdownExe("/s");

    /// <summary>Restarts the machine now (<c>shutdown.exe /r /t 0</c>).</summary>
    public static bool Restart() => RunShutdownExe("/r");

    /// <summary>Hibernates the machine; false when hibernation is disabled/rejected.</summary>
    public static bool Hibernate()
    {
        bool ok = Application.SetSuspendState(PowerState.Hibernate, force: false, disableWakeEvent: false);
        if (!ok)
        {
            Log.Error("hibernate: SetSuspendState(Hibernate) was rejected (hibernation may be disabled).");
        }

        return ok;
    }

    private static bool RunShutdownExe(string modeSwitch)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { modeSwitch, "/t", "0" },
                UseShellExecute = false,
            });
            if (process is null)
            {
                Log.Error($"shutdown.exe {modeSwitch} did not start.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"shutdown.exe {modeSwitch} failed: {ex.Message}");
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
