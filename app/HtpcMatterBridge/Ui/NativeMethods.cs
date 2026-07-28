using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// P/Invoke surface for the layered-window overlay technique (ADR-003 item 5:
/// <c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>) and the objective
/// non-activation / click-through checks used by <see cref="OverlayHudDemo"/>.
/// The only place these user32/gdi32 signatures are declared. Declared with
/// <see cref="LibraryImportAttribute"/> (ADR-005: compile-time marshalling for
/// all non-COM P/Invoke).
/// </summary>
internal static partial class NativeMethods
{
    internal const int GwlExstyle = -20;
    internal const int WsExLayered = 0x00080000;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;

    internal const uint UlwAlpha = 0x00000002;
    internal const byte AcSrcOver = 0x00;
    internal const byte AcSrcAlpha = 0x01;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;

    /// <summary>Destroys a native HICON (used by <see cref="TrayIcons"/> after cloning into a managed Icon).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    // The one flag that matters here: without it, SetWindowPos(HWND_TOPMOST, ...)
    // activates the window as a side effect of the z-order change — independent
    // of, and not prevented by, WS_EX_NOACTIVATE (that ex-style only blocks
    // click/alt-tab activation, not an explicit SetWindowPos call). WinForms'
    // own `Form.TopMost = true` does not pass this flag, which is why topmost
    // is applied here directly instead.
    internal const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point32
    {
        public int X;
        public int Y;

        public Point32(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size32
    {
        public int Width;
        public int Height;

        public Size32(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref Point32 pptDst,
        ref Size32 psize,
        IntPtr hdcSrc,
        ref Point32 pptSrc,
        int crKey,
        ref BlendFunction pblend,
        uint dwFlags);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfoHeader bmi,
        uint usage,
        out IntPtr bits,
        IntPtr hSection,
        uint offset);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiObj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    // Thread/queue-scoped "active window" — unlike GetForegroundWindow, this is
    // not subject to the OS anti-focus-stealing lock, so it is a meaningful
    // signal even when the calling process cannot win the desktop-wide
    // foreground (e.g. launched non-interactively by CI/automation).
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetActiveWindow();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(Point32 point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(IntPtr hWnd, int nIndex);

    /// <summary>
    /// Bitness-safe style read-back: <c>GetWindowLongPtr</c> does not exist on
    /// 32-bit Windows (there it is a header macro for <c>GetWindowLong</c>).
    /// </summary>
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
}
