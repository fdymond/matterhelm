using FsCheck.Xunit;
using MatterHelm.Sidecar;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>Bounded property/fuzz coverage for the IPC trust boundary (OpenSSF Scorecard Fuzzing).</summary>
public sealed class ProtocolPropertyTests
{
    private const string ReplaySeed = "184467,92821";

    [Property(MaxTest = 160, EndSize = 40, Replay = ReplaySeed)]
    public void EveryAcceptedSidecarFrameRoundTrips(byte variant, int first, int second, bool flag)
    {
        SidecarFrame expected = CreateSidecarFrame(variant, first, second, flag);
        string json = SerializeSidecarFrame(expected);

        SidecarParseResult result = Protocol.ParseSidecarFrame(json);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(expected, result.Frame);
    }

    [Property(MaxTest = 120, EndSize = 40, Replay = ReplaySeed)]
    public void EveryEmittedTrayFrameRoundTrips(byte variant, int first, int second, bool flag)
    {
        TrayFrame expected = CreateTrayFrame(variant, first, second, flag);

        string json = Protocol.Serialize(expected);
        TrayFrame parsed = ParseSerializedTrayFrame(json);

        Assert.Equal(expected, parsed);
    }

    [Property(MaxTest = 180, EndSize = 48, Replay = ReplaySeed)]
    public void ArbitraryStringsNeverThrowAndEveryRejectionReasonIsSafe(string? input)
    {
        SidecarParseResult result = Protocol.ParseSidecarFrame(input ?? string.Empty);

        if (!result.Success)
        {
            AssertSanitizedReason(result.Reason);
        }
    }

    [Property(MaxTest = 120, EndSize = 36, Replay = ReplaySeed)]
    public void ArbitraryInvalidJsonObjectsRejectWithoutThrowing(
        string? arbitraryKey,
        string? arbitraryValue,
        byte shape)
    {
        string json = CreateInvalidJsonObject(arbitraryKey ?? string.Empty, arbitraryValue ?? string.Empty, shape);

        SidecarParseResult result = Protocol.ParseSidecarFrame(json);

        Assert.False(result.Success);
        AssertSanitizedReason(result.Reason);
    }

    [Fact]
    public void HugeDeepWrongTypeAndUnicodeCorpusRejectsWithoutUnsafeReasons()
    {
        string[] corpus =
        [
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["v"] = Protocol.Version,
                ["type"] = "unknown\0\u001f\u0085\u009f\u202a\u202e\u2066\u2028\u2029" + new string('x', 512),
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["v"] = Protocol.Version,
                ["type"] = "hello",
                ["token"] = new string('t', 70_000),
                ["protocol"] = "wrong-type",
            }),
            BuildDeepObject(80),
            "{\"v\":5,\"type\":false,\"id\":[],\"name\":{},\"value\":null}",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["v"] = Protocol.Version,
                ["type"] = "hello",
                ["token"] = "token",
                ["protocol"] = Protocol.HandshakeProtocol,
                ["unknown\r\n\u0085\u202e\u2028"] = true,
            }),
        ];

        foreach (string json in corpus)
        {
            SidecarParseResult result = Protocol.ParseSidecarFrame(json);
            Assert.False(result.Success);
            AssertSanitizedReason(result.Reason);
        }
    }

    [Fact]
    public async Task FrameOverSizeLimitIsRejectedBeforeACompleteJsonObjectCanReachFieldParsing()
    {
        string oversized = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["v"] = Protocol.Version,
            ["type"] = "action",
            ["id"] = Guid.NewGuid().ToString("D"),
            ["name"] = "setVolume",
            ["value"] = "wrong-type-after-size-guard",
            ["padding"] = new string('x', 100_000),
        });
        using var socket = new FragmentedReceiveWebSocket(Encoding.UTF8.GetBytes(oversized));

        WebSocketException error = await Assert.ThrowsAsync<WebSocketException>(
            () => IpcServer.ReceiveTextFrameAsyncForTest(socket));

        Assert.Contains("exceeded 65536 bytes", error.Message, StringComparison.Ordinal);
        Assert.False(socket.EndOfMessageDelivered);
    }

    private static SidecarFrame CreateSidecarFrame(byte variant, int first, int second, bool flag)
    {
        Guid id = CreateGuid(first, second);
        return (variant % 14) switch
        {
            0 => new HelloFrame($"token-{id:D}"),
            1 => new BareActionFrame(id, BareActionName.PlayPause),
            2 => new BareActionFrame(id, BareActionName.Play),
            3 => new BareActionFrame(id, BareActionName.Pause),
            4 => new BareActionFrame(id, BareActionName.Next),
            5 => new BareActionFrame(id, BareActionName.Previous),
            6 => new BareActionFrame(id, BareActionName.PowerOn),
            7 => new BareActionFrame(id, BareActionName.PowerOff),
            8 => new CustomActionFrame(id, $"command-{(uint)first:x8}", flag),
            9 => new SetVolumeFrame(id, (int)((uint)first % 101)),
            10 => new SetMutedFrame(id, flag),
            11 => new PairingFrame($"MT:{(uint)first:X8}{(uint)second:X8}", $"{(uint)first % 10000:0000}-{(uint)second % 1000:000}"),
            12 when flag => new MatterStatusFrame(true, AdvertisementStatus.NotApplicable),
            12 => new MatterStatusFrame(false, AdvertisementStatus.Checking),
            _ => new MatterStatusFrame(false, (AdvertisementStatus)((uint)first % 3)),
        };
    }

    private static TrayFrame CreateTrayFrame(byte variant, int first, int second, bool flag)
    {
        Guid id = CreateGuid(first, second);
        return (variant % 4) switch
        {
            0 => new AckOkFrame(id),
            1 => new AckFailFrame(id),
            2 => new AckFailFrame(id, $"failure-{(uint)first:x8}"),
            _ => new StateFrame((int)((uint)first % 101), flag),
        };
    }

    private static Guid CreateGuid(int first, int second)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, first);
        BitConverter.TryWriteBytes(bytes[4..], second);
        BitConverter.TryWriteBytes(bytes[8..], ~first);
        BitConverter.TryWriteBytes(bytes[12..], ~second);
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static string SerializeSidecarFrame(SidecarFrame frame)
    {
        object wire = frame switch
        {
            HelloFrame hello => new { v = Protocol.Version, type = "hello", token = hello.Token, protocol = Protocol.HandshakeProtocol },
            BareActionFrame action => new { v = Protocol.Version, type = "action", id = action.Id.ToString("D"), name = BareActionWireName(action.Name) },
            CustomActionFrame action => new { v = Protocol.Version, type = "action", id = action.Id.ToString("D"), name = "custom", key = action.Key, on = action.On },
            SetVolumeFrame action => new { v = Protocol.Version, type = "action", id = action.Id.ToString("D"), name = "setVolume", value = action.Value },
            SetMutedFrame action => new { v = Protocol.Version, type = "action", id = action.Id.ToString("D"), name = "setMuted", value = action.Value },
            PairingFrame pairing => new { v = Protocol.Version, type = "pairing", qrPayload = pairing.QrPayload, manualCode = pairing.ManualCode },
            MatterStatusFrame status => new { v = Protocol.Version, type = "matterStatus", commissioned = status.Commissioned, advertisement = AdvertisementWireName(status.Advertisement) },
            _ => throw new ArgumentOutOfRangeException(nameof(frame)),
        };
        return JsonSerializer.Serialize(wire);
    }

    private static TrayFrame ParseSerializedTrayFrame(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(Protocol.Version, root.GetProperty("v").GetInt32());
        return root.GetProperty("type").GetString() switch
        {
            "ack" when root.GetProperty("ok").GetBoolean() =>
                new AckOkFrame(root.GetProperty("id").GetGuid()),
            "ack" => new AckFailFrame(
                root.GetProperty("id").GetGuid(),
                root.TryGetProperty("error", out JsonElement error) ? error.GetString() : null),
            "state" => new StateFrame(root.GetProperty("volume").GetInt32(), root.GetProperty("muted").GetBoolean()),
            string type => throw new InvalidOperationException($"Unexpected tray frame type {type}."),
            null => throw new InvalidOperationException("Serialized tray frame type was null."),
        };
    }

    private static string CreateInvalidJsonObject(string key, string value, byte shape) => (shape % 7) switch
    {
        0 => JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = 42, ["extra-" + key] = value }),
        1 => JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = Protocol.Version, ["type"] = "unknown-" + value }),
        2 => JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = Protocol.Version, ["type"] = "hello", ["token"] = "token", ["protocol"] = Protocol.HandshakeProtocol, ["unknown-" + key] = value }),
        3 => JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = Protocol.Version, ["type"] = "bad\0\u0085\u202e\u2028" + value }),
        4 => JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = Protocol.Version, ["type"] = "unknown", ["payload"] = new string('x', 70_000) }),
        5 => BuildDeepObject(65 + (shape % 16)),
        _ => JsonSerializer.Serialize(new object?[] { key, value, null, 42, false }),
    };

    private static string BuildDeepObject(int depth) =>
        string.Concat(Enumerable.Repeat("{\"nested\":", depth)) + "null" + new string('}', depth);

    private static void AssertSanitizedReason(string? reason)
    {
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.All(reason!, character =>
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            Assert.False(
                char.IsControl(character)
                || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator,
                $"Unsafe U+{(int)character:X4} in rejection reason.");
        });

        string[] embeddingPrefixes = ["unknown frame type \"", "tray-only frame type \"", "unknown action name \"", "unknown field \""];
        string? prefix = embeddingPrefixes.FirstOrDefault(candidate => reason.StartsWith(candidate, StringComparison.Ordinal));
        if (prefix is not null)
        {
            Assert.True(
                reason.Length <= prefix.Length + 64 + 2,
                $"Embedded rejection value exceeded its cap: {reason.Length} characters.");
        }
    }

    private static string BareActionWireName(BareActionName name) => name switch
    {
        BareActionName.PlayPause => "playPause",
        BareActionName.Play => "play",
        BareActionName.Pause => "pause",
        BareActionName.Next => "next",
        BareActionName.Previous => "previous",
        BareActionName.PowerOn => "powerOn",
        BareActionName.PowerOff => "powerOff",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string AdvertisementWireName(AdvertisementStatus status) => status switch
    {
        AdvertisementStatus.Checking => "checking",
        AdvertisementStatus.Visible => "visible",
        AdvertisementStatus.Missing => "missing",
        AdvertisementStatus.NotApplicable => "notApplicable",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private sealed class FragmentedReceiveWebSocket(byte[] payload) : WebSocket
    {
        private int _offset;

        internal bool EndOfMessageDelivered { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            int count = Math.Min(buffer.Count, payload.Length - _offset);
            payload.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            bool end = _offset == payload.Length;
            EndOfMessageDelivered |= end;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, end));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
