using System.Net;
using System.Net.Sockets;

namespace MatterHelm.Tests;

/// <summary>Shared plumbing for the integration-flavoured tests (real sockets, real node children).</summary>
internal static class TestSupport
{
    /// <summary>Locates node.exe: HTPC_DEMO_NODE, then PATH, then %USERPROFILE%\tools\node-*.</summary>
    internal static string RequireNodeExe()
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

        throw new InvalidOperationException(
            "node.exe not found — set HTPC_DEMO_NODE or add node to PATH (supervisor tests spawn stub node children).");
    }

    /// <summary>Reserves a currently-free loopback port (HttpListener cannot bind port 0 itself).</summary>
    internal static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Polls <paramref name="condition"/> until true or <paramref name="timeout"/> elapses (then throws, naming <paramref name="what"/>).</summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Thread-safe log capture to inject as a supervisor/server log sink.</summary>
    internal sealed class LogCapture
    {
        private readonly Lock _gate = new();
        private readonly List<(string Level, string Message)> _entries = [];

        internal void Sink(string level, string message)
        {
            lock (_gate)
            {
                _entries.Add((level, message));
            }
        }

        internal IReadOnlyList<(string Level, string Message)> Snapshot()
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }

        internal bool Contains(string level, string messageSubstring) =>
            Snapshot().Any(e => e.Level == level && e.Message.Contains(messageSubstring, StringComparison.Ordinal));

        internal bool ContainsMessage(string messageSubstring) =>
            Snapshot().Any(e => e.Message.Contains(messageSubstring, StringComparison.Ordinal));
    }
}
