using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Actions;

/// <summary>
/// Shared <c>SendInput</c> interop for <see cref="MediaKeys"/> (keyboard) and
/// <see cref="DisplayPower"/> (mouse wake nudge). Centralised because the INPUT
/// union layout must match the native definition exactly and duplicating it
/// invites drift.
/// </summary>
internal static partial class NativeInput
{
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint KeyEventFExtendedKey = 0x0001;
    public const uint KeyEventFKeyUp = 0x0002;
    public const uint MouseEventFMove = 0x0001;

    /// <summary>Native INPUT struct (type discriminator + union).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    /// <summary>Union of MOUSEINPUT/KEYBDINPUT; both start at offset 0 per the native definition.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    /// <summary>Native MOUSEINPUT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>Native KEYBDINPUT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    /// <summary>
    /// Sends the given events, returning true iff all were injected.
    /// Failures (e.g. blocked by a UIPI-elevated foreground window) are logged.
    /// </summary>
    public static bool Send(Input[] inputs, string description)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            Log.Error($"SendInput ({description}) injected {sent}/{inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        return true;
    }
}
