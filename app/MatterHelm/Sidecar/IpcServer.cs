using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MatterHelm.Diagnostics;

namespace MatterHelm.Sidecar;

/// <summary>
/// Loopback WebSocket server the sidecar connects to. Per ADR-003 item 1 this
/// is <see cref="HttpListener"/> with the literal prefix
/// <c>http://localhost:{port}/</c> — that exact string is exempt from http.sys
/// URL-ACL reservations (no admin rights); never substitute <c>127.0.0.1</c>.
///
/// Trust model (BLUEPRINT §2.3): the first inbound frame must be a valid
/// <c>hello</c> carrying the session token within <c>helloTimeout</c>, or the
/// socket closes. After auth, every inbound frame goes through
/// <see cref="Protocol.ParseSidecarFrame"/>; the first invalid frame closes
/// the socket (reason logged at WARN — the token value itself is never
/// logged). Token comparison is constant-time.
///
/// Single-client policy: one sidecar at a time; while a connection holds the
/// slot (authenticating or authenticated), later connections are rejected
/// with HTTP 409. Chosen over replace-on-connect so an unauthenticated local
/// process can never bump the live, token-proven sidecar; a crashed sidecar's
/// socket is detected promptly (loopback RST), freeing the slot well inside
/// the supervisor's minimum restart backoff.
///
/// Threading: <see cref="ActionReceived"/>/<see cref="PairingReceived"/>/
/// <see cref="ClientChanged"/> fire on thread-pool threads — marshalling to
/// the UI thread is the subscriber's job (S2-5).
/// </summary>
public sealed class IpcServer : IDisposable
{
    private const int MaxFrameBytes = 64 * 1024;

    private readonly HttpListener _listener = new();
    private readonly byte[] _tokenUtf8;
    private readonly TimeSpan _helloTimeout;
    private readonly Action<string, string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _acceptLoop;
    private WebSocket? _authedClient;
    private int _clientSlot; // 0 = free, 1 = held by a connection (Interlocked)
    private bool _disposed;

    /// <summary>Creates the server (call <see cref="Start"/> to listen).</summary>
    /// <param name="port">Loopback port (BLUEPRINT default 39531).</param>
    /// <param name="token">Expected session token for the hello handshake.</param>
    /// <param name="helloTimeout">How long a fresh connection may take to send its hello; default 5 s.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="Log"/>. Injectable for tests/demos.</param>
    public IpcServer(int port, string token, TimeSpan? helloTimeout = null, Action<string, string>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentException.ThrowIfNullOrEmpty(token);
        _tokenUtf8 = Encoding.UTF8.GetBytes(token);
        _helloTimeout = helloTimeout ?? TimeSpan.FromSeconds(5);
        _log = log ?? DefaultLog;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>An authenticated sidecar sent a valid action frame. Fires on a pool thread.</summary>
    public event EventHandler<ActionFrame>? ActionReceived;

    /// <summary>An authenticated sidecar sent a valid pairing frame. Fires on a pool thread.</summary>
    public event EventHandler<PairingFrame>? PairingReceived;

    /// <summary>Authenticated-client presence changed (true = connected, false = gone). Fires on a pool thread.</summary>
    public event EventHandler<bool>? ClientChanged;

    /// <summary>True while an authenticated sidecar is connected.</summary>
    public bool HasClient => Volatile.Read(ref _authedClient) is not null;

    /// <summary>Starts listening and accepting connections.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Sends an outbound frame to the authenticated sidecar. Returns false
    /// (after one WARN, never a throw) when no sidecar is connected or the
    /// send fails — Matter-side callers degrade gracefully per the blueprint.
    /// </summary>
    public async Task<bool> SendAsync(TrayFrame frame)
    {
        WebSocket? socket = Volatile.Read(ref _authedClient);
        if (socket is null || socket.State != WebSocketState.Open)
        {
            _log("WARN", $"IPC: no connected sidecar; dropping outbound {frame.GetType().Name}.");
            return false;
        }

        byte[] payload = Encoding.UTF8.GetBytes(Protocol.Serialize(frame));
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, _cts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _log("WARN", $"IPC: send failed ({ex.Message}); frame dropped.");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Stops accepting, closes the client socket, and waits for the accept loop. Idempotent.</summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        Volatile.Read(ref _authedClient)?.Abort();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous shutdown; safe off the UI thread (S2-5 must call <see cref="StopAsync"/> or dispose on a worker).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        _listener.Close();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    /// <summary>
    /// Receives one complete message as text; null means the peer closed.
    /// Message type is not checked — a binary frame simply fails the JSON
    /// parse, which is the real (content-based) trust boundary.
    /// </summary>
    private static async Task<string?> ReceiveTextFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket
                .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                    // Peer is gone either way.
                }

                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxFrameBytes)
            {
                throw new WebSocketException($"inbound message exceeded {MaxFrameBytes} bytes");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static void DefaultLog(string level, string message)
    {
        switch (level)
        {
            case "ERROR":
                Log.Error(message);
                break;
            case "WARN":
                Log.Warn(message);
                break;
            default:
                Log.Info(message);
                break;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                _log("WARN", $"IPC: accept failed ({ex.Message}).");
                continue;
            }

            try
            {
                await HandleContextAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("WARN", $"IPC: connection handling failed ({ex.Message}).");
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }

        if (Interlocked.CompareExchange(ref _clientSlot, 1, 0) != 0)
        {
            _log("WARN", "IPC: rejected concurrent connection (single-client policy).");
            context.Response.StatusCode = (int)HttpStatusCode.Conflict;
            context.Response.Close();
            return;
        }

        HttpListenerWebSocketContext webSocketContext;
        try
        {
            webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _clientSlot, 0);
            throw;
        }

        _ = Task.Run(() => ServeClientAsync(webSocketContext.WebSocket));
    }

    private async Task ServeClientAsync(WebSocket socket)
    {
        bool authenticated = false;
        try
        {
            if (!await AuthenticateAsync(socket).ConfigureAwait(false))
            {
                return;
            }

            authenticated = true;
            Volatile.Write(ref _authedClient, socket);
            AppMetrics.IpcClientConnects.Add(1);
            _log("INFO", "IPC: sidecar connected and authenticated.");
            Raise(() => ClientChanged?.Invoke(this, true));
            await ReceiveLoopAsync(socket).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server shutdown.
        }
        catch (WebSocketException ex)
        {
            _log("WARN", $"IPC: socket error ({ex.Message}).");
        }
        catch (Exception ex)
        {
            _log("ERROR", $"IPC: unexpected connection failure: {ex}");
        }
        finally
        {
            Volatile.Write(ref _authedClient, null);
            Interlocked.Exchange(ref _clientSlot, 0);
            socket.Dispose();
            if (authenticated)
            {
                AppMetrics.IpcClientDisconnects.Add(1);
                _log("INFO", "IPC: sidecar disconnected.");
                Raise(() => ClientChanged?.Invoke(this, false));
            }
        }
    }

    private async Task<bool> AuthenticateAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(_helloTimeout);
        string? text;
        try
        {
            text = await ReceiveTextFrameAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            _log("WARN", "IPC: no hello frame within timeout; closing.");
            socket.Abort(); // The receive is wedged; a graceful close handshake cannot proceed.
            return false;
        }

        if (text is null)
        {
            return false; // Peer closed before authenticating.
        }

        SidecarParseResult result = Protocol.ParseSidecarFrame(text);
        if (result.Success && result.Frame is HelloFrame hello)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), _tokenUtf8))
            {
                return true;
            }

            // The presented token value is deliberately not logged.
            _log("WARN", "IPC: hello token mismatch; closing.");
        }
        else
        {
            string reason = result.Success ? "first frame must be hello" : result.Reason!;
            _log("WARN", $"IPC: invalid handshake frame ({reason}); closing.");
        }

        await CloseAsync(socket, "unauthorized").ConfigureAwait(false);
        return false;
    }

    private async Task ReceiveLoopAsync(WebSocket socket)
    {
        while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            string? text = await ReceiveTextFrameAsync(socket, _cts.Token).ConfigureAwait(false);
            if (text is null)
            {
                return;
            }

            SidecarParseResult result = Protocol.ParseSidecarFrame(text);
            // A second hello after the handshake is as invalid as any bad
            // frame — BLUEPRINT §2.3 mandates close-on-first-invalid-frame.
            if (!result.Success || result.Frame is HelloFrame)
            {
                string reason = result.Success ? "unexpected hello after handshake" : result.Reason!;
                _log("WARN", $"IPC: invalid frame ({reason}); closing.");
                await CloseAsync(socket, "invalid frame").ConfigureAwait(false);
                return;
            }

            switch (result.Frame)
            {
                case ActionFrame action:
                    Raise(() => ActionReceived?.Invoke(this, action));
                    break;
                case PairingFrame pairing:
                    Raise(() => PairingReceived?.Invoke(this, pairing));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Subscriber exceptions must never tear down the receive loop.</summary>
    private void Raise(Action fire)
    {
        try
        {
            fire();
        }
        catch (Exception ex)
        {
            _log("ERROR", $"IPC: event subscriber threw: {ex}");
        }
    }
}
