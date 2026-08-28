using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace MatterHelm.Sidecar;

// ---------------------------------------------------------------------------
// Typed frames — sidecar -> tray app
// ---------------------------------------------------------------------------

/// <summary>Base of every frame the sidecar sends to the tray app.</summary>
/// <remarks>
/// The wire-level <c>v</c> field is a validated literal
/// (<see cref="Protocol.Version"/>), so parsed records do not carry it; the
/// parser rejects anything else and the serializer always emits it.
/// </remarks>
public abstract record SidecarFrame;

/// <summary>
/// First frame the sidecar must send; the tray app closes the socket on
/// anything else arriving first. <c>protocol</c> is the breaking-change
/// counter (validated literal <see cref="Protocol.HandshakeProtocol"/>),
/// independent of the per-message <c>v</c>.
/// </summary>
public sealed record HelloFrame(string Token) : SidecarFrame;

/// <summary>Base of every <c>action</c> frame variant; <paramref name="Id"/> correlates the eventual ack.</summary>
public abstract record ActionFrame(Guid Id) : SidecarFrame;

/// <summary>The payload-less action names (each is "press the button").</summary>
public enum BareActionName
{
    /// <summary>Media play/pause toggle.</summary>
    PlayPause,

    /// <summary>Next track.</summary>
    Next,

    /// <summary>Previous track.</summary>
    Previous,

    /// <summary>Stateful power endpoint turned on.</summary>
    PowerOn,

    /// <summary>Stateful power endpoint turned off.</summary>
    PowerOff,
}

/// <summary>An action that carries no payload (playPause/next/previous/powerOn/powerOff).</summary>
public sealed record BareActionFrame(Guid Id, BareActionName Name) : ActionFrame(Id);

/// <summary>
/// <c>custom</c> (protocol v2, ADR-004 §3): a user-defined momentary endpoint
/// fired; <paramref name="Key"/> is its stable kebab-case slug — the config
/// key, the Matter endpoint id, and the wire identifier. Carries no
/// <c>value</c> field.
/// </summary>
public sealed record CustomActionFrame(Guid Id, string Key) : ActionFrame(Id);

/// <summary><c>setVolume</c>; <paramref name="Value"/> is an integer 0-100 (percent, not 0-254).</summary>
public sealed record SetVolumeFrame(Guid Id, int Value) : ActionFrame(Id);

/// <summary><c>setMuted</c>; <paramref name="Value"/> is the target mute state.</summary>
public sealed record SetMutedFrame(Guid Id, bool Value) : ActionFrame(Id);

/// <summary>
/// Commissioning payloads for the pairing window. <paramref name="QrPayload"/>
/// always starts with the fixed Matter onboarding prefix <c>MT:</c> — a
/// payload without it is a bridge bug and is rejected at parse.
/// </summary>
public sealed record PairingFrame(string QrPayload, string ManualCode) : SidecarFrame;

/// <summary>Whether an uncommissioned bridge is actually discoverable over mDNS.</summary>
public enum AdvertisementStatus
{
    /// <summary>The first active observation is still running.</summary>
    Checking,
    /// <summary>A commissionable Matter record was observed.</summary>
    Visible,
    /// <summary>No commissionable Matter record was observed before timeout.</summary>
    Missing,
    /// <summary>The node is commissioned, so commissionable advertising does not apply.</summary>
    NotApplicable,
}

/// <summary>Matter lifecycle plus commissionable-advertisement health (protocol v3).</summary>
public sealed record MatterStatusFrame(bool Commissioned, AdvertisementStatus Advertisement) : SidecarFrame;

// ---------------------------------------------------------------------------
// Typed frames — tray app -> sidecar
// ---------------------------------------------------------------------------

/// <summary>Base of every frame the tray app sends to the sidecar.</summary>
public abstract record TrayFrame;

/// <summary>
/// Successful ack for action <paramref name="Id"/>. Mirroring protocol.ts,
/// success and failure are separate types so an <c>ok: true</c> ack cannot
/// carry an <c>error</c> field even by accident.
/// </summary>
public sealed record AckOkFrame(Guid Id) : TrayFrame;

/// <summary>Failed ack for action <paramref name="Id"/>; <paramref name="Error"/> is optional context.</summary>
public sealed record AckFailFrame(Guid Id, string? Error = null) : TrayFrame;

/// <summary>System volume/mute snapshot, sent on connect and on every change.</summary>
public sealed record StateFrame : TrayFrame
{
    /// <summary>Creates a state frame; <paramref name="volume"/> must be 0-100.</summary>
    public StateFrame(int volume, bool muted)
    {
        // Internal invariant, not a trust boundary: the tray app constructs
        // these itself, so an out-of-range volume is a bug — fail loud.
        ArgumentOutOfRangeException.ThrowIfNegative(volume);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 100);
        Volume = volume;
        Muted = muted;
    }

    /// <summary>System volume percent, 0-100.</summary>
    public int Volume { get; }

    /// <summary>System mute state.</summary>
    public bool Muted { get; }
}

// ---------------------------------------------------------------------------
// Parse result
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of parsing one inbound sidecar frame: either a typed
/// <see cref="Frame"/> or a human-readable rejection <see cref="Reason"/>
/// (surfaced so the close-on-invalid-frame rule can log why).
/// </summary>
public sealed class SidecarParseResult
{
    private SidecarParseResult(SidecarFrame? frame, string? reason)
    {
        Frame = frame;
        Reason = reason;
    }

    /// <summary>The parsed frame, when <see cref="Success"/>.</summary>
    public SidecarFrame? Frame { get; }

    /// <summary>Why the frame was rejected, when not <see cref="Success"/>. Never contains field values.</summary>
    public string? Reason { get; }

    /// <summary>True iff the input was a valid sidecar frame.</summary>
    [MemberNotNullWhen(true, nameof(Frame))]
    [MemberNotNullWhen(false, nameof(Reason))]
    public bool Success => Frame is not null;

    internal static SidecarParseResult Ok(SidecarFrame frame) => new(frame, null);

    internal static SidecarParseResult Fail(string reason) => new(null, reason);
}

// ---------------------------------------------------------------------------
// Protocol
// ---------------------------------------------------------------------------

/// <summary>
/// Typed mirror of <c>bridge/src/ipc/protocol.ts</c>, the normative frame
/// contract (BLUEPRINT §2.3): one JSON object per WebSocket message. Parsing
/// is the trust boundary for inbound sidecar frames and is strict everywhere —
/// unknown fields, unknown <c>type</c>/<c>name</c> discriminators, wrong
/// <c>v</c>, malformed uuids, out-of-range or fractional volumes are all
/// rejections with a surfaced reason. Serialization covers the outbound tray
/// frames (<c>ack</c>/<c>state</c>). Pure: no sockets, no logging.
/// </summary>
public static class Protocol
{
    /// <summary>Per-message revision carried in <c>v</c>; additive evolution only (see protocol.ts). Version 3 adds Matter lifecycle and advertisement health; only this exact value parses.</summary>
    public const int Version = 3;

    /// <summary>Breaking-change counter carried in <c>hello.protocol</c>; bumps require an ADR. Unchanged by v3 — no breaking field changes.</summary>
    public const int HandshakeProtocol = 1;

    private static readonly string[] _helloKeys = ["v", "type", "token", "protocol"];
    private static readonly string[] _bareActionKeys = ["v", "type", "id", "name"];
    private static readonly string[] _valueActionKeys = ["v", "type", "id", "name", "value"];
    private static readonly string[] _customActionKeys = ["v", "type", "id", "name", "key"];
    private static readonly string[] _pairingKeys = ["v", "type", "qrPayload", "manualCode"];
    private static readonly string[] _matterStatusKeys = ["v", "type", "commissioned", "advertisement"];

    /// <summary>
    /// Parses one raw WebSocket text message as a sidecar-&gt;tray frame. The
    /// only place inbound sidecar frames may be trusted from — never cast,
    /// always parse (mirrors <c>parseSidecarFrame</c> in protocol.ts).
    /// </summary>
    public static SidecarParseResult ParseSidecarFrame(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return SidecarParseResult.Fail("message is not valid JSON");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SidecarParseResult.Fail("frame must be a JSON object");
            }

            string type = "";
            string? err = RequireString(root, "type", ref type);
            if (err is not null)
            {
                return SidecarParseResult.Fail(err);
            }

            return type switch
            {
                "hello" => ParseHello(root),
                "action" => ParseAction(root),
                "pairing" => ParsePairing(root),
                "matterStatus" => ParseMatterStatus(root),
                "ack" or "state" => SidecarParseResult.Fail($"tray-only frame type \"{type}\""),
                _ => SidecarParseResult.Fail($"unknown frame type \"{type}\""),
            };
        }
    }

    /// <summary>Serializes an outbound tray-&gt;sidecar frame to its wire form (camelCase, <c>v</c> included).</summary>
    public static string Serialize(TrayFrame frame)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            switch (frame)
            {
                case AckOkFrame ok:
                    writer.WriteString("type", "ack");
                    writer.WriteString("id", ok.Id.ToString("D"));
                    writer.WriteBoolean("ok", true);
                    break;
                case AckFailFrame fail:
                    writer.WriteString("type", "ack");
                    writer.WriteString("id", fail.Id.ToString("D"));
                    writer.WriteBoolean("ok", false);
                    if (fail.Error is not null)
                    {
                        writer.WriteString("error", fail.Error);
                    }

                    break;
                case StateFrame state:
                    writer.WriteString("type", "state");
                    writer.WriteNumber("volume", state.Volume);
                    writer.WriteBoolean("muted", state.Muted);
                    break;
                default:
                    throw new ArgumentException($"unknown tray frame type {frame.GetType().Name}", nameof(frame));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static SidecarParseResult ParseHello(JsonElement obj)
    {
        string token = "";
        int protocol = 0;
        string? err = CheckKeys(obj, _helloKeys)
            ?? CheckVersion(obj)
            ?? RequireString(obj, "token", ref token)
            ?? RequireInt(obj, "protocol", ref protocol);
        if (err is not null)
        {
            return SidecarParseResult.Fail(err);
        }

        if (token.Length == 0)
        {
            return SidecarParseResult.Fail("\"token\" must be a non-empty string");
        }

        if (protocol != HandshakeProtocol)
        {
            return SidecarParseResult.Fail($"unsupported \"protocol\" (expected {HandshakeProtocol})");
        }

        return SidecarParseResult.Ok(new HelloFrame(token));
    }

    private static SidecarParseResult ParseAction(JsonElement obj)
    {
        string name = "";
        string? err = RequireString(obj, "name", ref name);
        if (err is not null)
        {
            return SidecarParseResult.Fail(err);
        }

        string[] allowedKeys = name switch
        {
            "setVolume" or "setMuted" => _valueActionKeys,
            "custom" => _customActionKeys,
            _ => _bareActionKeys,
        };
        Guid id = Guid.Empty;
        err = CheckKeys(obj, allowedKeys)
            ?? CheckVersion(obj)
            ?? RequireUuid(obj, ref id);
        if (err is not null)
        {
            return SidecarParseResult.Fail(err);
        }

        switch (name)
        {
            case "playPause":
                return SidecarParseResult.Ok(new BareActionFrame(id, BareActionName.PlayPause));
            case "next":
                return SidecarParseResult.Ok(new BareActionFrame(id, BareActionName.Next));
            case "previous":
                return SidecarParseResult.Ok(new BareActionFrame(id, BareActionName.Previous));
            case "powerOn":
                return SidecarParseResult.Ok(new BareActionFrame(id, BareActionName.PowerOn));
            case "powerOff":
                return SidecarParseResult.Ok(new BareActionFrame(id, BareActionName.PowerOff));
            case "setVolume":
            {
                int value = 0;
                err = RequireInt(obj, "value", ref value);
                if (err is not null)
                {
                    return SidecarParseResult.Fail(err);
                }

                if (value is < 0 or > 100)
                {
                    return SidecarParseResult.Fail("\"value\" must be an integer between 0 and 100");
                }

                return SidecarParseResult.Ok(new SetVolumeFrame(id, value));
            }

            case "setMuted":
            {
                bool value = false;
                err = RequireBool(obj, "value", ref value);
                if (err is not null)
                {
                    return SidecarParseResult.Fail(err);
                }

                return SidecarParseResult.Ok(new SetMutedFrame(id, value));
            }

            case "custom":
            {
                string key = "";
                err = RequireString(obj, "key", ref key);
                if (err is not null)
                {
                    return SidecarParseResult.Fail(err);
                }

                // Same slug rule as the config schema and the bridge's zod
                // side (CommandKey doc) — never reveal the offending value.
                if (!CommandKey.IsValid(key))
                {
                    return SidecarParseResult.Fail(
                        $"\"key\" must be a kebab-case slug of at most {CommandKey.MaxLength} characters");
                }

                return SidecarParseResult.Ok(new CustomActionFrame(id, key));
            }

            default:
                return SidecarParseResult.Fail($"unknown action name \"{name}\"");
        }
    }

    private static SidecarParseResult ParsePairing(JsonElement obj)
    {
        string qrPayload = "";
        string manualCode = "";
        string? err = CheckKeys(obj, _pairingKeys)
            ?? CheckVersion(obj)
            ?? RequireString(obj, "qrPayload", ref qrPayload)
            ?? RequireString(obj, "manualCode", ref manualCode);
        if (err is not null)
        {
            return SidecarParseResult.Fail(err);
        }

        if (!qrPayload.StartsWith("MT:", StringComparison.Ordinal))
        {
            return SidecarParseResult.Fail("\"qrPayload\" must start with \"MT:\"");
        }

        if (manualCode.Length == 0)
        {
            return SidecarParseResult.Fail("\"manualCode\" must be a non-empty string");
        }

        return SidecarParseResult.Ok(new PairingFrame(qrPayload, manualCode));
    }

    private static SidecarParseResult ParseMatterStatus(JsonElement obj)
    {
        bool commissioned = false;
        string advertisement = "";
        string? err = CheckKeys(obj, _matterStatusKeys)
            ?? CheckVersion(obj)
            ?? RequireBool(obj, "commissioned", ref commissioned)
            ?? RequireString(obj, "advertisement", ref advertisement);
        if (err is not null)
        {
            return SidecarParseResult.Fail(err);
        }

        AdvertisementStatus status = advertisement switch
        {
            "checking" => AdvertisementStatus.Checking,
            "visible" => AdvertisementStatus.Visible,
            "missing" => AdvertisementStatus.Missing,
            "notApplicable" => AdvertisementStatus.NotApplicable,
            _ => (AdvertisementStatus)(-1),
        };
        if (!Enum.IsDefined(status))
        {
            return SidecarParseResult.Fail("\"advertisement\" has an unsupported value");
        }

        if (commissioned != (status == AdvertisementStatus.NotApplicable))
        {
            return SidecarParseResult.Fail(
                commissioned
                    ? "a commissioned node must use advertisement \"notApplicable\""
                    : "an uncommissioned node cannot use advertisement \"notApplicable\"");
        }

        return SidecarParseResult.Ok(new MatterStatusFrame(commissioned, status));
    }

    // -- field helpers: each returns null on success or a rejection reason ---

    private static string? CheckKeys(JsonElement obj, string[] allowed)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                return $"unknown field \"{property.Name}\"";
            }
        }

        return null;
    }

    private static string? CheckVersion(JsonElement obj)
    {
        if (!obj.TryGetProperty("v", out JsonElement v))
        {
            return "missing field \"v\"";
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int value) || value != Version)
        {
            return $"\"v\" must be the integer {Version}";
        }

        return null;
    }

    private static string? RequireString(JsonElement obj, string name, ref string value)
    {
        if (!obj.TryGetProperty(name, out JsonElement element))
        {
            return $"missing field \"{name}\"";
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return $"\"{name}\" must be a string";
        }

        value = element.GetString()!;
        return null;
    }

    private static string? RequireInt(JsonElement obj, string name, ref int value)
    {
        if (!obj.TryGetProperty(name, out JsonElement element))
        {
            return $"missing field \"{name}\"";
        }

        // TryGetInt32 rejects fractional wire forms like 100.5 — the strict
        // integer check protocol.ts expresses with z.int().
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            return $"\"{name}\" must be an integer";
        }

        return null;
    }

    private static string? RequireBool(JsonElement obj, string name, ref bool value)
    {
        if (!obj.TryGetProperty(name, out JsonElement element))
        {
            return $"missing field \"{name}\"";
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return $"\"{name}\" must be a boolean";
        }

        value = element.GetBoolean();
        return null;
    }

    private static string? RequireUuid(JsonElement obj, ref Guid id)
    {
        string raw = "";
        string? err = RequireString(obj, "id", ref raw);
        if (err is not null)
        {
            return err;
        }

        // Parity with zod v4's z.uuid() (the normative side, protocol.ts):
        // canonical hyphenated form AND RFC 9562 constraints — version nibble
        // 1-8, variant nibble [89ab] — with nil/max special-cased. Bare
        // Guid.TryParseExact("D") accepts any hex in the 8-4-4-4-12 shape,
        // which zod rejects; since we echo the id into the ack, a looser
        // parse here could emit an ack the sidecar's parser kills the socket
        // over (S2-R finding 3).
        if (!Guid.TryParseExact(raw, "D", out id))
        {
            return "\"id\" must be a canonical uuid";
        }

        if (raw is "00000000-0000-0000-0000-000000000000" or "ffffffff-ffff-ffff-ffff-ffffffffffff")
        {
            return null;
        }

        char version = char.ToLowerInvariant(raw[14]);
        char variant = char.ToLowerInvariant(raw[19]);
        if (version is < '1' or > '8' || variant is not ('8' or '9' or 'a' or 'b'))
        {
            id = Guid.Empty;
            return "\"id\" must be an RFC 9562 uuid (version 1-8, variant 8/9/a/b)";
        }

        return null;
    }
}
