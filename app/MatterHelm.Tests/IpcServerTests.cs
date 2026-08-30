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
[Collection(IpcListenerSuite.Name)]
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
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{_actionId}}","name":"setVolume","value":40}""");
        ActionFrame action = await Await(received.Task, "action event");
        SetVolumeFrame setVolume = Assert.IsType<SetVolumeFrame>(action);
        Assert.Equal(40, setVolume.Value);
        Assert.Equal(_actionId, setVolume.Id);

        Assert.True(await server.SendAsync(new AckOkFrame(_actionId)));
        (WebSocketMessageType type, string text, _) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal($$"""{"v":5,"type":"ack","id":"{{_actionId}}","ok":true}""", text);
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
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, """{"v":5,"type":"pairing","qrPayload":"MT:ABC","manualCode":"3497-011-2332"}""");
        PairingFrame frame = await Await(received.Task, "pairing event");
        Assert.Equal("MT:ABC", frame.QrPayload);

        Assert.True(await server.SendAsync(new StateFrame(12, muted: true)));
        (_, string text, _) = await ReceiveAsync(client);
        Assert.Equal("""{"v":5,"type":"state","volume":12,"muted":true}""", text);
    }

    [Fact]
    public async Task MatterStatusFramesSurfaceAsTypedEvents()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var received = new TaskCompletionSource<MatterStatusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.MatterStatusReceived += (_, status) => received.TrySetResult(status);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");
        await SendAsync(client,
            """{"v":5,"type":"matterStatus","commissioned":false,"advertisement":"missing"}""");

        MatterStatusFrame frame = await Await(received.Task, "Matter status event");
        Assert.False(frame.Commissioned);
        Assert.Equal(AdvertisementStatus.Missing, frame.Advertisement);
    }

    [Fact]
    public async Task WrongTokenHelloClosesTheSocketWithoutAuthenticating()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, """{"v":5,"type":"hello","token":"wrong-token","protocol":1}""");

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
        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{_actionId}}","name":"playPause"}""");

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
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{_actionId}}","name":"setVolume","value":101}""");

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
        await SendAsync(first, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");

        using var second = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() =>
            second.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None));
        Assert.True(server.HasClient); // The original session survives.
    }

    [Fact]
    public async Task DelayedActionExecutionDoesNotBlockReceivingLaterFramesAndAcksStayCorrelated()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        Guid secondActionId = Guid.Parse("7f9be2e6-9d0a-4f7e-9a76-1a2b3c4d5e70");
        using var firstActionEntered = new ManualResetEventSlim();
        using var releaseFirstAction = new ManualResetEventSlim();
        var pairingReceived = new TaskCompletionSource<PairingFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionOrder = new List<Guid>();
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.ActionReceived += (_, action) =>
        {
            lock (actionOrder)
            {
                actionOrder.Add(action.Id);
            }

            if (action.Id == _actionId)
            {
                firstActionEntered.Set();
                releaseFirstAction.Wait();
            }

            Assert.True(server.SendAsync(new AckOkFrame(action.Id)).GetAwaiter().GetResult());
        };
        server.PairingReceived += (_, pairing) => pairingReceived.TrySetResult(pairing);
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");
        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{_actionId}}","name":"play"}""");
        Assert.True(firstActionEntered.Wait(TimeSpan.FromSeconds(5)));

        await SendAsync(client, """{"v":5,"type":"pairing","qrPayload":"MT:LATER","manualCode":"1111-222-3333"}""");
        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{secondActionId}}","name":"pause"}""");
        PairingFrame pairing = await Await(pairingReceived.Task, "pairing frame behind delayed action");
        Assert.Equal("MT:LATER", pairing.QrPayload);

        releaseFirstAction.Set();
        (_, string firstAck, _) = await ReceiveAsync(client);
        (_, string secondAck, _) = await ReceiveAsync(client);
        Assert.Contains(_actionId.ToString(), firstAck, StringComparison.Ordinal);
        Assert.Contains(secondActionId.ToString(), secondAck, StringComparison.Ordinal);
        lock (actionOrder)
        {
            Assert.Equal([_actionId, secondActionId], actionOrder);
        }
    }

    [Fact]
    public async Task SaturatedActionQueueLogsOneWarningAndResumesInOrder()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        var capture = new TestSupport.LogCapture();
        using var firstActionEntered = new ManualResetEventSlim();
        using var releaseFirstAction = new ManualResetEventSlim();
        var processed = new List<Guid>();
        using var server = new IpcServer(port, Token, log: capture.Sink);
        server.ActionReceived += (_, action) =>
        {
            if (!firstActionEntered.IsSet)
            {
                firstActionEntered.Set();
                releaseFirstAction.Wait();
            }

            lock (processed)
            {
                processed.Add(action.Id);
            }
        };
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");
        Guid[] actionIds = [.. Enumerable.Range(0, 70).Select(_ => Guid.NewGuid())];
        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{actionIds[0]}}","name":"play"}""");
        Assert.True(firstActionEntered.Wait(TimeSpan.FromSeconds(5)));
        foreach (Guid id in actionIds[1..])
        {
            await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{id}}","name":"pause"}""");
        }

        await TestSupport.WaitUntilAsync(
            () => capture.Contains("WARN", "action queue reached its 64-frame capacity"),
            TimeSpan.FromSeconds(5),
            "bounded action queue saturation warning");
        releaseFirstAction.Set();
        await TestSupport.WaitUntilAsync(
            () =>
            {
                lock (processed)
                {
                    return processed.Count == actionIds.Length;
                }
            },
            TimeSpan.FromSeconds(5),
            "saturated action queue to drain");

        Assert.Single(
            capture.Snapshot(),
            entry => entry.Level == "WARN" && entry.Message.Contains("action queue reached", StringComparison.Ordinal));
        lock (processed)
        {
            Assert.Equal(actionIds, processed);
        }
    }

    [Fact]
    public async Task StopAsyncDrainsAnInFlightActionBeforeCollaboratorsCanBeDisposed()
    {
        int port = TestSupport.GetFreeLoopbackPort();
        using var actionEntered = new ManualResetEventSlim();
        using var releaseAction = new ManualResetEventSlim();
        bool collaboratorDisposed = false;
        bool touchedDisposedCollaborator = false;
        using var server = new IpcServer(port, Token, log: (_, _) => { });
        server.ActionReceived += (_, _) =>
        {
            actionEntered.Set();
            releaseAction.Wait();
            touchedDisposedCollaborator = collaboratorDisposed;
        };
        server.Start();

        using ClientWebSocket client = await ConnectAsync(port);
        await SendAsync(client, $$"""{"v":5,"type":"hello","token":"{{Token}}","protocol":1}""");
        await TestSupport.WaitUntilAsync(() => server.HasClient, TimeSpan.FromSeconds(5), "hello authentication");
        await SendAsync(client, $$"""{"v":5,"type":"action","id":"{{_actionId}}","name":"play"}""");
        Assert.True(actionEntered.Wait(TimeSpan.FromSeconds(5)));

        Task stopping = server.StopAsync();
        Assert.False(stopping.IsCompleted, "shutdown must wait for the active action subscriber");
        releaseAction.Set();
        await stopping;
        collaboratorDisposed = true;

        Assert.False(touchedDisposedCollaborator);
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

