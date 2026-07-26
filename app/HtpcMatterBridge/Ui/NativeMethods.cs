using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// P/Invoke surface for the layered-window overlay technique (ADR-003 item 5:
/// <c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>) and the objective
/// non-activation / click-through checks used by <see cref="OverlayHudDemo"/>.
/// The only place these user32/gdi32 signatures are declared.
/// </summary>
internal static class NativeMethods
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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref Point32 pptDst,
        ref Size32 psize,
        IntPtr hdcSrc,
        ref Point32 pptSrc,
        int crKey,
        ref BlendFunction pblend,
        uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfoHeader bmi,
        uint usage,
        out IntPtr bits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiObj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    // Thread/queue-scoped "active window" — unlike GetForegroundWindow, this is
    // not subject to the OS anti-focus-stealing lock, so it is a meaningful
    // signal even when the calling process cannot win the desktop-wide
    // foreground (e.g. launched non-interactively by CI/automation).
    [DllImport("user32.dll")]
    internal static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    /// <summary>
    /// Bitness-safe style read-back: <c>GetWindowLongPtr</c> does not exist on
    /// 32-bit Windows (there it is a header macro for <c>GetWindowLong</c>).
    /// </summary>
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
}
