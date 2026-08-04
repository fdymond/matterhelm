using System.Net.Sockets;

namespace HtpcMatterBridge.Demos;

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
}
