using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Actions;

/// <summary>
/// Display and power control. Blanks displays with <c>SC_MONITORPOWER 2</c> sent to a
/// private message-only window (ADR-003: never <c>HWND_BROADCAST</c>), wakes them with
/// a net-zero relative mouse nudge (<c>SC_MONITORPOWER -1</c> is unreliable since Win8),
/// and suspends the machine via WinForms <see cref="Application.SetSuspendState"/>
/// (which handles enabling the shutdown privilege).
/// </summary>
public sealed partial class DisplayPower : IDisposable
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private static readonly nint HwndMessage = -3;

    private readonly MessageOnlyWindow _window = new();

    /// <summary>Puts all displays into their low-power (off) state.</summary>
    public bool DisplaysOff()
    {
        // SendMessage's return value carries no success signal for SC_MONITORPOWER.
        _ = SendMessageW(_window.Handle, WmSysCommand, ScMonitorPower, MonitorOff);
        return true;
    }

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

    /// <summary>Destroys the message-only window.</summary>
    public void Dispose() => _window.DestroyHandle();

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Invisible message-only window; exists only as a SendMessage target.</summary>
    private sealed class MessageOnlyWindow : NativeWindow
    {
        public MessageOnlyWindow() => CreateHandle(new CreateParams { Parent = HwndMessage });
    }
}
