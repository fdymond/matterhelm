using System.Net.WebSockets;
using System.Text;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for <see cref="IpcServer"/> against a real
/// <see cref="ClientWebSocket"/> on an ephemeral loopback port: handshake,
/// round-trips, and every close-on-invalid path from BLUEPRINT §2.3.
/// </summary>
public sealed class IpcServerTests
{
    private const string Token = "test-session-token-0123456789";
    private static readonly Guid _actionId = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");

    [Fact]
    public async Task ValidHelloAuthenticatesActionRoundTripsAndAckIsDelivered()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        var received = new TaskCompletionSource<ActionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.ActionReceived += (_, action) => received.TrySetResult(action);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":2,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, $$"""{"v":2,"type":"action","id":"{{_actionId}}","name":"setVolume","value":40}""");
        ActionFrame action = await Await(received.Task, "action event");
        SetVolumeFrame setVolume = Assert.IsType<SetVolumeFrame>(action);
        Assert.Equal(40, setVolume.Value);
        Assert.Equal(_actionId, setVolume.Id);

        Assert.True(await server.SendAsync(new AckOkFrame(_actionId)));
        (WebSocketMessageType type, string text, _) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal($$"""{"v":2,"type":"ack","id":"{{_actionId}}","ok":true}""", text);
    }

    [Fact]
    public async Task PairingFramesSurfaceAsEventsAndStateFramesReachTheClient()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var received = new TaskCompletionSource<PairingFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.PairingReceived += (_, pairing) => received.TrySetResult(pairing);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":2,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, """{"v":2,"type":"pairing","qrPayload":"MT:ABC","manualCode":"3497-011-2332"}""");
        PairingFrame frame = await Await(received.Task, "pairing event");
        Assert.Equal("MT:ABC", frame.QrPayload);

        Assert.True(await server.SendAsync(new StateFrame(12, muted: true)));
        (_, string text, _) = await ReceiveAsync(client);
        Assert.Equal("""{"v":2,"type":"state","volume":12,"muted":true}""", text);
    }

    [Fact]
    public async Task WrongTokenHelloClosesTheSocketWithoutAuthenticating()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, """{"v":2,"type":"hello","token":"wrong-token","protocol":1}""");

        await AssertClosedAsync(client);
        Assert.False(server.HasClient);
        Assert.True(capture.Contains("WARN", "token mismatch"), "expected a WARN about the token mismatch");
        // The token values themselves must never appear in the log.
        Assert.False(capture.ContainsMessage(Token));
        Assert.False(capture.ContainsMessage("wrong-token"));
    }

    [Fact]
    public async Task GarbageFirstFrameClosesTheSocket()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, "this is not json");

        await AssertClosedAsync(client);
        Assert.False(server.HasClient);
        Assert.True(capture.Contains("WARN", "invalid handshake frame"), "expected a WARN naming the rejection");
    }

    [Fact]
    public async Task ValidNonHelloFirstFrameClosesTheSocket()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":2,"type":"action","id":"{{_actionId}}","name":"playPause"}""");

        await AssertClosedAsync(client);
        Assert.False(server.HasClient);
    }

    [Fact]
    public async Task PostAuthInvalidFrameClosesTheSocketAndFiresNoEvent()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        bool actionFired = false;
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.ActionReceived += (_, _) => actionFired = true;
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":2,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, $$"""{"v":2,"type":"action","id":"{{_actionId}}","name":"setVolume","value":101}""");

        await AssertClosedAsync(client);
        await TestSupport.WaitUntilAsync(() => !server.HasClient, TimeSpan.FromSeconds(5), "client teardown");
        Assert.False(actionFired);
        // The server logs on its own thread; the client can observe the close
        // before the WARN lands in the capture — wait, don't assert instantly.
        await TestSupport.WaitUntilAsync(
            () => capture.Contains("WARN", "invalid frame"),
            TimeSpan.FromSeconds(5),
            "WARN naming the rejection");
    }

    [Fact]
    public async Task HelloTimeoutClosesASilentConnection()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var server = new IpcServer(port, Token, helloTimeout: TimeSpan.FromMilliseconds(250), log: capture.Sink);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        // Send nothing; the server must give up on its own.
        await AssertClosedAsync(client);
        // Same server-thread logging race as above: the close can reach the
        // client before the WARN reaches the capture (seen on CI runners).
        await TestSupport.WaitUntilAsync(
            () => capture.Contains("WARN", "no hello frame within timeout"),
            TimeSpan.FromSeconds(5),
            "WARN about the hello timeout");
    }

    [Fact]
    public async Task SecondConnectionIsRejectedWhileTheFirstIsActive()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.Start();

        using ClientWebSocket first = await ConnectAsync(port);
        await SendAsync(first, $$"""{"v":2,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        using var second = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() =>
            second.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None));
        Assert.True(server.HasClient); // The original session survives.
    }

    [Fact]
    public async Task SendWithNoConnectedSidecarReturnsFalseWithoutThrowing()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.Start();

        Assert.False(await server.SendAsync(new StateFrame(50, muted: false)));
        Assert.True(capture.Contains("WARN", "no connected sidecar"), "expected a WARN about the dropped frame");
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        return client;
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    private static async Task<(WebSocketMessageType Type, string Text, WebSocketCloseStatus? CloseStatus)> ReceiveAsync(
        ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.MessageType, "", result.CloseStatus);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()), null);
            }
        }
    }

    /// <summary>The server may close gracefully (close frame) or abort (exception); both count as "socket closed".</summary>
    private static async Task AssertClosedAsync(ClientWebSocket client)
    {
        try
        {
            (WebSocketMessageType type, _, _) = await ReceiveAsync(client);
            Assert.Equal(WebSocketMessageType.Close, type);
        }
        catch (WebSocketException)
        {
            // Abrupt close — equally "closed".
        }
    }

    private static async Task<T> Await<T>(Task<T> task, string what)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (!ReferenceEquals(completed, task))
        {
            throw new TimeoutException($"timed out waiting for {what}");
        }

        return await task;
    }
}
