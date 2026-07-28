using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

namespace HtpcMatterBridge.Sidecar;

/// <summary>
/// Story S2-1 acceptance-evidence harness, run via
/// <c>HtpcMatterBridge.exe --demo-sidecar-chaos</c>. Part 1 spawns a stub node
/// child that heartbeats and crashes every ~2 s, runs the supervisor for
/// ~15 s, and asserts auto-restarts with growing, jitter-bounded backoff
/// delays (production constants). Part 2 starts an <see cref="IpcServer"/>,
/// connects a real <see cref="ClientWebSocket"/>, presents a wrong-token
/// hello, and asserts the server closes the socket. Results go to stdout
/// (parent console, if any) and to <c>sidecar-chaos-demo-results.txt</c> next
/// to the exe; exit code 0 iff every check passed. Uses its own log sink —
/// never the %APPDATA% file log. Not part of the production tray flow.
/// </summary>
internal static class SidecarChaosDemo
{
    private const string ResultsFileName = "sidecar-chaos-demo-results.txt";
    private const int AttachParentProcess = -1;

    /// <summary>Runs the full demo. Returns the process exit code: 0 iff every check passed.</summary>
    internal static int Run()
    {
        _ = AttachConsole(AttachParentProcess); // WinExe has no console; borrow the parent's if present.
        var gate = new object();
        var lines = new List<string>();
        bool allPassed = true;

        void Emit(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }

            Console.WriteLine(line);
        }

        void Check(bool pass, string what)
        {
            allPassed &= pass;
            Emit($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        Emit($"S2-1 sidecar chaos demo — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string? node = ResolveNodeExe();
        if (node is null)
        {
            Emit("FAIL  node.exe not found (set HTPC_DEMO_NODE or add node to PATH)");
            WriteResults(lines, allPassed: false);
            return 1;
        }

        Emit($"node: {node}");

        try
        {
            RunCrashRestartPart(node, Emit, Check, gate);
            RunWrongTokenPart(Emit, Check);
        }
        catch (Exception ex)
        {
            allPassed = false;
            Emit($"FAIL  demo crashed: {ex}");
        }

        WriteResults(lines, allPassed);
        return allPassed ? 0 : 1;
    }

    private static void RunCrashRestartPart(string node, Action<string> emit, Action<bool, string> check, object gate)
    {
        emit("--- part 1: kill sidecar -> auto-restart with backoff (production constants) ---");
        var restartDelays = new List<int>();
        var startCount = 0;
        const string script =
            "let n = 0;" +
            "setInterval(() => console.log('heartbeat ' + (++n)), 500);" +
            "setTimeout(() => { console.error('simulated crash'); process.exit(1); }, 2000);";
        using (var supervisor = new SidecarSupervisor(
            new SidecarSpec(node, ["-e", script], Environment.CurrentDirectory),
            ipcPort: 39531,
            storageDir: Path.Combine(Path.GetTempPath(), "htpc-chaos-demo"),
            log: (level, message) => emit($"    [{level}] {message}")))
        {
            supervisor.ChildStarted += (_, _) =>
            {
                lock (gate)
                {
                    startCount++;
                }
            };
            supervisor.RestartScheduled += (_, delay) =>
            {
                lock (gate)
                {
                    restartDelays.Add(delay);
                }
            };
            supervisor.Start();
            Thread.Sleep(15_000);
            supervisor.Stop();
        }

        int starts;
        int[] delays;
        lock (gate)
        {
            starts = startCount;
            delays = [.. restartDelays];
        }

        emit($"child starts observed: {starts}; scheduled restart delays: [{string.Join(", ", delays)}] ms");
        check(starts >= 3, $"child auto-restarted at least twice (starts = {starts})");
        check(delays.Length >= 2, $"at least two restart delays were scheduled ({delays.Length})");
        for (int i = 0; i < delays.Length; i++)
        {
            int ideal = Math.Min(30_000, 500 << i);
            int low = (int)(ideal * 0.75) - 1;
            int high = (int)(ideal * 1.25) + 1;
            check(
                delays[i] >= low && delays[i] <= high,
                $"delay {i} = {delays[i]} ms is within ±25 % of {ideal} ms");
        }

        for (int i = 1; i < delays.Length; i++)
        {
            check(delays[i] > delays[i - 1], $"delay {i} ({delays[i]} ms) grew over delay {i - 1} ({delays[i - 1]} ms)");
        }
    }

    private static void RunWrongTokenPart(Action<string> emit, Action<bool, string> check)
    {
        emit("--- part 2: wrong-token hello -> socket closed ---");
        string expectedToken = Guid.NewGuid().ToString("N");
        int port = GetFreeLoopbackPort();
        using var server = new IpcServer(port, expectedToken, log: (level, message) => emit($"    [{level}] {message}"));
        server.Start();
        emit($"IpcServer listening on http://localhost:{port}/");

        using var client = new ClientWebSocket();
        client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).GetAwaiter().GetResult();
        byte[] badHello = Encoding.UTF8.GetBytes(
            /*lang=json*/ """{"v":2,"type":"hello","token":"wrong-token","protocol":1}""");
        client.SendAsync(badHello, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        bool closed = false;
        WebSocketCloseStatus? closeStatus = null;
        try
        {
            var buffer = new byte[1024];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            WebSocketReceiveResult result = client
                .ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token)
                .GetAwaiter()
                .GetResult();
            closed = result.MessageType == WebSocketMessageType.Close;
            closeStatus = result.CloseStatus;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            closed = ex is WebSocketException; // Abrupt close counts as closed too.
        }

        emit($"client observed: closed={closed}, closeStatus={closeStatus?.ToString() ?? "(none)"}");
        check(closed, "server closed the socket after the wrong-token hello");
        check(!server.HasClient, "server never treated the wrong-token client as authenticated");
    }

    private static void WriteResults(List<string> lines, bool allPassed)
    {
        string resultsPath = Path.Combine(AppContext.BaseDirectory, ResultsFileName);
        string report = string.Join(Environment.NewLine, lines) + Environment.NewLine +
            $"OVERALL: {(allPassed ? "PASS" : "FAIL")}" + Environment.NewLine;
        File.WriteAllText(resultsPath, report);
        Console.WriteLine($"OVERALL: {(allPassed ? "PASS" : "FAIL")}");
        Console.WriteLine($"(results file: {resultsPath})");
    }

    /// <summary>Demo-only node resolution: HTPC_DEMO_NODE, then PATH, then %USERPROFILE%\tools\node-*.</summary>
    private static string? ResolveNodeExe()
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

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
