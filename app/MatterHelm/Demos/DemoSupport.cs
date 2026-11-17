using System.Net.Sockets;

namespace MatterHelm.Demos;

/// <summary>
/// Shared helpers for the standalone acceptance-demo harnesses
/// (<see cref="WiredDemo"/>, <see cref="PairingWindowDemo"/>,
/// <see cref="SettingsWindowDemo"/>, and <see cref="Sidecar.SidecarChaosDemo"/>):
/// demo-only node.exe resolution, reserving an ephemeral loopback port, and
/// pumping the STA message loop while no <c>Application.Run</c> is active.
/// None of this is part of the production tray flow.
/// </summary>
internal static class DemoSupport
{
    private const double MinimumNonBackgroundFraction = 0.01;
    private static string? _isolatedStateRoot;

    /// <summary>Repoints every app default to a process-owned temp root before a demo can touch state.</summary>
    internal static void InitializeIsolatedState(string mode)
    {
        if (_isolatedStateRoot is not null)
        {
            Console.WriteLine($"isolated state root: {_isolatedStateRoot}");
            return;
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "MatterHelmDemos",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        AppPaths.Root = root;
        Log.LogDirectory = Path.Combine(root, "logs");
        _isolatedStateRoot = root;
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteIsolatedStateRoot();
        Console.WriteLine($"{mode} isolated state root: {root}");
    }

    private static void DeleteIsolatedStateRoot()
    {
        string? root = _isolatedStateRoot;
        if (root is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Demo state is temporary and process-exit cleanup is best effort.
        }
    }

    /// <summary>Demo-only node resolution: HTPC_DEMO_NODE, then PATH, then %USERPROFILE%\tools\node-*.</summary>
    internal static string? ResolveNodeExe()
    {
        string? env = Environment.GetEnvironmentVariable("HTPC_DEMO_NODE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "node.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry; skip.
                }
            }
        }

        string tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools");
        if (Directory.Exists(tools))
        {
            foreach (string dir in Directory.EnumerateDirectories(tools, "node-*"))
            {
                string candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Reserves a currently-free loopback port (HttpListener cannot bind port 0 itself).</summary>
    internal static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Pumps the STA message loop for at least <paramref name="milliseconds"/> so a window finishes laying out (no <c>Application.Run</c> is active in these harnesses).</summary>
    internal static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        do
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        while (Environment.TickCount64 < deadline);
    }

    /// <summary>Returns true when a client-area sample contains both color diversity and a meaningful amount of non-background content.</summary>
    internal static bool HasRenderedContent(Bitmap bitmap, int minimumDistinctColors = 4)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Rectangle sampleBounds = ClientSampleBounds(bitmap.Size);
        var colorCounts = new Dictionary<int, int>();
        int sampledPixels = 0;
        int stepX = Math.Max(1, sampleBounds.Width / 100);
        int stepY = Math.Max(1, sampleBounds.Height / 100);
        for (int y = sampleBounds.Top; y < sampleBounds.Bottom; y += stepY)
        {
            for (int x = sampleBounds.Left; x < sampleBounds.Right; x += stepX)
            {
                int color = bitmap.GetPixel(x, y).ToArgb();
                colorCounts.TryGetValue(color, out int count);
                colorCounts[color] = count + 1;
                sampledPixels++;
            }
        }

        int backgroundPixels = colorCounts.Count == 0 ? 0 : colorCounts.Values.Max();
        double nonBackgroundFraction = sampledPixels == 0
            ? 0
            : (sampledPixels - backgroundPixels) / (double)sampledPixels;
        Console.WriteLine(
            $"rendered-content: client sample {sampleBounds.Width}x{sampleBounds.Height}, "
            + $"{sampledPixels} pixels, {colorCounts.Count} distinct colors, "
            + $"{nonBackgroundFraction:P2} non-background "
            + $"(requires >= {minimumDistinctColors} colors and >= {MinimumNonBackgroundFraction:P2})");
        return colorCounts.Count >= minimumDistinctColors
            && nonBackgroundFraction >= MinimumNonBackgroundFraction;
    }

    private static Rectangle ClientSampleBounds(Size bitmapSize)
    {
        int horizontalFrame = Math.Min(SystemInformation.FrameBorderSize.Width, Math.Max(0, bitmapSize.Width / 4));
        int bottomFrame = Math.Min(SystemInformation.FrameBorderSize.Height, Math.Max(0, bitmapSize.Height / 4));
        int captionAndTopFrame = Math.Min(
            SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height,
            Math.Max(0, bitmapSize.Height / 3));
        var client = Rectangle.FromLTRB(
            horizontalFrame,
            captionAndTopFrame,
            bitmapSize.Width - horizontalFrame,
            bitmapSize.Height - bottomFrame);
        return client.Width > 0 && client.Height > 0
            ? client
            : new Rectangle(Point.Empty, bitmapSize);
    }
}
