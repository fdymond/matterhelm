using System.Text.Json;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Specification tests for <see cref="Protocol"/>, ported case-for-case from
/// <c>bridge/src/ipc/protocol.test.ts</c> so both sides of the wire enforce
/// the identical contract (protocol v5; ADR-004 §3 plus dedicated play/pause). Sidecar-direction cases
/// test <see cref="Protocol.ParseSidecarFrame"/>; tray-direction cases
/// (ack/state) test <see cref="Protocol.Serialize"/> and the record
/// invariants.
/// </summary>
public static class ProtocolTests
{
    private const string Uuid1 = "123e4567-e89b-12d3-a456-426614174000";

    private static SidecarParseResult Parse(string json) => Protocol.ParseSidecarFrame(json);

    public sealed class VersionConstants
    {
        [Fact]
        public void ProtocolVersionIs5AndHandshakeStays1()
        {
            // Dedicated play/pause actions are additive, so v is 5;
            // hello.protocol is the breaking-change counter and stays 1.
            Assert.Equal(5, Protocol.Version);
            Assert.Equal(1, Protocol.HandshakeProtocol);
        }
    }

    public sealed class HelloFrames
    {
        [Fact]
        public void AcceptsAWellFormedHelloFrame()
        {
            SidecarParseResult result = Parse("""{"v":5,"type":"hello","token":"session-token","protocol":1}""");
            Assert.True(result.Success);
            HelloFrame hello = Assert.IsType<HelloFrame>(result.Frame);
            Assert.Equal("session-token", hello.Token);
        }

        [Fact]
        public void RejectsAnEmptyToken()
        {
            SidecarParseResult result = Parse("""{"v":5,"type":"hello","token":"","protocol":1}""");
            Assert.False(result.Success);
            Assert.Contains("token", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAMissingVField()
        {
            SidecarParseResult result = Parse("""{"type":"hello","token":"session-token","protocol":1}""");
            Assert.False(result.Success);
            Assert.Contains("v", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsTheSupersededV1Revision()
        {
            // Version-bump proof: after dedicated play/pause landed only v:5 parses.
            Assert.False(Parse("""{"v":1,"type":"hello","token":"session-token","protocol":1}""").Success);
        }

        [Fact]
        public void RejectsAVFieldFromAFutureRevision()
        {
            Assert.False(Parse("""{"v":6,"type":"hello","token":"session-token","protocol":1}""").Success);
        }

        [Fact]
        public void RejectsTheWrongTypeDiscriminator()
        {
            // hello fields under type "action" must not parse as anything.
            Assert.False(Parse("""{"v":5,"type":"action","token":"session-token","protocol":1}""").Success);
        }

        [Fact]
        public void RejectsAProtocolFieldOtherThan1()
        {
            SidecarParseResult result = Parse("""{"v":5,"type":"hello","token":"session-token","protocol":2}""");
            Assert.False(result.Success);
            Assert.Contains("protocol", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUnknownExtraKeysStrictBoundary()
        {
            SidecarParseResult result =
                Parse("""{"v":5,"type":"hello","token":"session-token","protocol":1,"extra":"nope"}""");
            Assert.False(result.Success);
            Assert.Contains("extra", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownHelloFieldReasonRemovesUnicodeLogControlsBeforeAuthentication()
        {
            string field = "extra\u0085\u202Efield";
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["v"] = 5,
                ["type"] = "hello",
                ["token"] = "session-token",
                ["protocol"] = 1,
                [field] = true,
            });

            SidecarParseResult result = Parse(json);

            Assert.False(result.Success);
            Assert.DoesNotContain('\u0085', result.Reason!);
            Assert.DoesNotContain('\u202E', result.Reason!);
            Assert.Contains("extrafield", result.Reason, StringComparison.Ordinal);
        }
    }

    public sealed class BareActionFrames
    {
        public static TheoryData<string, BareActionName> BareNames => new()
        {
            { "playPause", BareActionName.PlayPause },
            { "play", BareActionName.Play },
            { "pause", BareActionName.Pause },
            { "next", BareActionName.Next },
            { "previous", BareActionName.Previous },
            { "powerOn", BareActionName.PowerOn },
            { "powerOff", BareActionName.PowerOff },
        };

        [Theory]
        [MemberData(nameof(BareNames))]
        public void AcceptsAWellFormedBareAction(string name, BareActionName expected)
        {
            SidecarParseResult result = Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"{{name}}"}""");
            Assert.True(result.Success);
            BareActionFrame action = Assert.IsType<BareActionFrame>(result.Frame);
            Assert.Equal(expected, action.Name);
            Assert.Equal(Guid.Parse(Uuid1), action.Id);
        }

        [Theory]
        [InlineData("playPause")]
        [InlineData("play")]
        [InlineData("pause")]
        [InlineData("next")]
        [InlineData("previous")]
        [InlineData("powerOn")]
        [InlineData("powerOff")]
        public void RejectsABareActionCarryingAnUnexpectedValueField(string name)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"{{name}}","value":1}""");
            Assert.False(result.Success);
            Assert.Contains("value", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAnUnknownActionName()
        {
            SidecarParseResult result = Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"rewind"}""");
            Assert.False(result.Success);
            Assert.Contains("rewind", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownActionReasonStripsControlCharactersAndCapsTheEmbeddedName()
        {
            string name = "rewind\r\n\u001bWARN-" + new string('x', 80);
            string json = JsonSerializer.Serialize(new { v = 5, type = "action", id = Uuid1, name });

            SidecarParseResult result = Parse(json);

            Assert.False(result.Success);
            Assert.All(result.Reason!, character => Assert.False(char.IsControl(character)));
            string embedded = result.Reason!.Split('"')[1];
            Assert.Equal(65, embedded.Length);
            Assert.EndsWith("…", embedded, StringComparison.Ordinal);
        }

        [Fact]
        public void TruncatedReasonNeverSplitsASurrogatePair()
        {
            string name = new string('x', 62) + "\U0001F680" + "tail";
            string json = JsonSerializer.Serialize(new { v = 5, type = "action", id = Uuid1, name });

            SidecarParseResult result = Parse(json);

            string embedded = result.Reason!.Split('"')[1];
            Assert.Equal(63, embedded.Length);
            Assert.EndsWith("…", embedded, StringComparison.Ordinal);
            Assert.DoesNotContain(embedded, char.IsSurrogate);
        }

        [Fact]
        public void TruncatedReasonAppendsEllipsisWhenASurrogatePairStraddlesTheCut()
        {
            string name = new string('x', 63) + "\U0001F680" + "tail";
            string json = JsonSerializer.Serialize(new { v = 5, type = "action", id = Uuid1, name });

            SidecarParseResult result = Parse(json);

            string embedded = result.Reason!.Split('"')[1];
            Assert.Equal(new string('x', 63) + "…", embedded);
            Assert.DoesNotContain(embedded, char.IsSurrogate);
        }

        [Fact]
        public void RejectsAMalformedUuidInId()
        {
            Assert.False(Parse("""{"v":5,"type":"action","id":"not-a-uuid","name":"playPause"}""").Success);
        }

        // RFC 9562 parity with zod v4's z.uuid() (S2-R finding 3): hex in the
        // right shape is not enough — version nibble must be 1-8 and variant
        // 8/9/a/b, with nil and (lowercase) max special-cased, exactly as the
        // normative protocol.ts side accepts/rejects.
        [Theory]
        [InlineData("12345678-1234-c234-c234-123456789012")] // version 'c'
        [InlineData("12345678-1234-0234-9234-123456789012")] // version '0'
        [InlineData("12345678-1234-4234-c234-123456789012")] // variant 'c'
        [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")] // uppercase max: zod's literal is lowercase-only
        public void RejectsANonRfc9562UuidThatZodWouldReject(string id)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{id}}","name":"playPause"}""");
            Assert.False(result.Success);
        }

        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000")] // nil
        [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")] // max
        [InlineData("ABCDEF01-2345-4678-89AB-CDEF01234567")] // uppercase hex, valid v4
        public void AcceptsEveryUuidFormZodAccepts(string id)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{id}}","name":"playPause"}""");
            Assert.True(result.Success);
        }

        [Fact]
        public void RejectsAMissingId()
        {
            Assert.False(Parse("""{"v":5,"type":"action","name":"playPause"}""").Success);
        }

        [Fact]
        public void RejectsTheSupersededV1Revision()
        {
            Assert.False(Parse($$"""{"v":1,"type":"action","id":"{{Uuid1}}","name":"playPause"}""").Success);
        }

        [Fact]
        public void RejectsAWrongVField()
        {
            Assert.False(Parse($$"""{"v":2,"type":"action","id":"{{Uuid1}}","name":"playPause"}""").Success);
        }

        [Fact]
        public void RejectsTheSupersededV3Revision()
        {
            Assert.False(Parse($$"""{"v":3,"type":"action","id":"{{Uuid1}}","name":"play"}""").Success);
        }

        [Fact]
        public void RejectsUnknownExtraKeysStrictBoundary()
        {
            Assert.False(
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"playPause","extra":"nope"}""").Success);
        }
    }

    public sealed class CustomActionFrames
    {
        // Mirrors the bridge's protocol.test.ts custom-action cases (ADR-004
        // §3): key is a kebab-case slug, at most 64 characters, no value field.
        [Theory]
        [InlineData("movie-mode")]
        [InlineData("a")]
        [InlineData("7")]
        [InlineData("a-b-c")]
        [InlineData("42-inch-tv")]
        public void AcceptsAWellFormedCustomActionAndPreservesTheKey(string key)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"{{key}}","on":true}""");
            Assert.True(result.Success);
            CustomActionFrame custom = Assert.IsType<CustomActionFrame>(result.Frame);
            Assert.Equal(key, custom.Key);
            Assert.True(custom.On);
            Assert.Equal(Guid.Parse(Uuid1), custom.Id);
        }

        [Fact]
        public void PreservesTheOffEdge()
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode","on":false}""");

            Assert.True(result.Success);
            Assert.False(Assert.IsType<CustomActionFrame>(result.Frame).On);
        }

        [Fact]
        public void RejectsAMissingOnEdge()
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode"}""");

            Assert.False(result.Success);
            Assert.Contains("on", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void AcceptsAKeyOfExactlyTheMaximumLength()
        {
            string key = new('a', CommandKey.MaxLength);
            Assert.True(
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"{{key}}","on":true}""").Success);
        }

        [Fact]
        public void RejectsAKeyOverTheMaximumLength()
        {
            string key = new('a', CommandKey.MaxLength + 1);
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"{{key}}","on":true}""");
            Assert.False(result.Success);
            Assert.Contains("key", result.Reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")] // empty
        [InlineData("Movie-Mode")] // uppercase
        [InlineData("movie_mode")] // underscore
        [InlineData("movie mode")] // space
        [InlineData("-movie")] // leading hyphen
        [InlineData("movie-")] // trailing hyphen
        [InlineData("movie--mode")] // empty segment
        [InlineData("café")] // non-ascii
        public void RejectsANonSlugKey(string key)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"{{key}}","on":true}""");
            Assert.False(result.Success);
            Assert.Contains("key", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAKeyWithATrailingNewline()
        {
            // Parity trap: .NET's regex $ also matches before a trailing
            // newline, JavaScript's does not — the shared rule uses \z so
            // "movie-mode\n" rejects on both sides (see CommandKey).
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode\n","on":true}""");
            Assert.False(result.Success);
            Assert.Contains("key", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAMissingKey()
        {
            SidecarParseResult result = Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","on":true}""");
            Assert.False(result.Success);
            Assert.Contains("key", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsANonStringKey()
        {
            Assert.False(
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":42,"on":true}""").Success);
        }

        [Fact]
        public void RejectsAStrayValueFieldCustomCarriesNoValue()
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode","on":true,"value":1}""");
            Assert.False(result.Success);
            Assert.Contains("value", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAMissingId()
        {
            Assert.False(Parse("""{"v":5,"type":"action","name":"custom","key":"movie-mode","on":true}""").Success);
        }

        [Fact]
        public void RejectsTheSupersededV1Revision()
        {
            Assert.False(
                Parse($$"""{"v":1,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode","on":true}""").Success);
        }

        [Fact]
        public void RejectsUnknownExtraKeysStrictBoundary()
        {
            Assert.False(Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"movie-mode","on":true,"extra":"nope"}""").Success);
        }

        [Fact]
        public void RejectionReasonsNeverContainTheOffendingKeyValue()
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"Secret-Value","on":true}""");
            Assert.False(result.Success);
            Assert.DoesNotContain("Secret-Value", result.Reason, StringComparison.Ordinal);
        }
    }

    public sealed class SetVolumeFrames
    {
        [Theory]
        [InlineData(0)]
        [InlineData(40)]
        [InlineData(100)]
        public void AcceptsInRangeVolumesIncludingBoundaries(int value)
        {
            SidecarParseResult result =
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setVolume","value":{{value}}}""");
            Assert.True(result.Success);
            SetVolumeFrame frame = Assert.IsType<SetVolumeFrame>(result.Frame);
            Assert.Equal(value, frame.Value);
        }

        [Fact]
        public void RejectsAMissingValueField()
        {
            SidecarParseResult result = Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setVolume"}""");
            Assert.False(result.Success);
            Assert.Contains("value", result.Reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("101")]
        [InlineData("100.5")]
        [InlineData("\"40\"")]
        public void RejectsOutOfRangeFractionalAndStringVolumes(string rawValue)
        {
            Assert.False(
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setVolume","value":{{rawValue}}}""").Success);
        }
    }

    public sealed class SetMutedFrames
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AcceptsAWellFormedSetMutedAction(bool value)
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setMuted","value":{{(value ? "true" : "false")}}}""");
            Assert.True(result.Success);
            SetMutedFrame frame = Assert.IsType<SetMutedFrame>(result.Frame);
            Assert.Equal(value, frame.Value);
        }

        [Fact]
        public void RejectsAMissingValueField()
        {
            Assert.False(Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setMuted"}""").Success);
        }

        [Theory]
        [InlineData("\"true\"")]
        [InlineData("1")]
        public void RejectsNonBooleanValues(string rawValue)
        {
            Assert.False(
                Parse($$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setMuted","value":{{rawValue}}}""").Success);
        }
    }

    public sealed class PairingFrames
    {
        [Fact]
        public void AcceptsAWellFormedPairingFrame()
        {
            SidecarParseResult result = Parse(
                """{"v":5,"type":"pairing","qrPayload":"MT:Y.K9042C00KA0648G00","manualCode":"3497-011-2332"}""");
            Assert.True(result.Success);
            PairingFrame pairing = Assert.IsType<PairingFrame>(result.Frame);
            Assert.Equal("MT:Y.K9042C00KA0648G00", pairing.QrPayload);
            Assert.Equal("3497-011-2332", pairing.ManualCode);
        }

        [Fact]
        public void RejectsAQrPayloadMissingTheMtPrefix()
        {
            SidecarParseResult result = Parse(
                """{"v":5,"type":"pairing","qrPayload":"Y.K9042C00KA0648G00","manualCode":"3497-011-2332"}""");
            Assert.False(result.Success);
            Assert.Contains("MT:", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAQrPayloadWithALowercaseMtPrefix()
        {
            Assert.False(Parse(
                """{"v":5,"type":"pairing","qrPayload":"mt:Y.K9042C00KA0648G00","manualCode":"3497-011-2332"}""").Success);
        }

        [Fact]
        public void RejectsAnEmptyManualCode()
        {
            Assert.False(
                Parse("""{"v":5,"type":"pairing","qrPayload":"MT:Y.K9042C00KA0648G00","manualCode":""}""").Success);
        }

        [Fact]
        public void RejectsAMissingQrPayload()
        {
            Assert.False(Parse("""{"v":5,"type":"pairing","manualCode":"3497-011-2332"}""").Success);
        }

        [Fact]
        public void RejectsTheWrongTypeDiscriminator()
        {
            Assert.False(Parse(
                """{"v":5,"type":"hello","qrPayload":"MT:Y.K9042C00KA0648G00","manualCode":"3497-011-2332"}""").Success);
        }

        [Fact]
        public void RejectsUnknownExtraKeysStrictBoundary()
        {
            Assert.False(Parse(
                """{"v":5,"type":"pairing","qrPayload":"MT:Y.K9042C00KA0648G00","manualCode":"3497-011-2332","extra":"nope"}""").Success);
        }
    }

    public sealed class MatterStatusFrames
    {
        [Theory]
        [InlineData(false, "checking", AdvertisementStatus.Checking)]
        [InlineData(false, "visible", AdvertisementStatus.Visible)]
        [InlineData(false, "missing", AdvertisementStatus.Missing)]
        [InlineData(true, "notApplicable", AdvertisementStatus.NotApplicable)]
        public void AcceptsConsistentLifecycleAndAdvertisementState(
            bool commissioned,
            string wireStatus,
            AdvertisementStatus expected)
        {
            SidecarParseResult result = Parse(
                $$"""{"v":5,"type":"matterStatus","commissioned":{{commissioned.ToString().ToLowerInvariant()}},"advertisement":"{{wireStatus}}"}""");
            Assert.True(result.Success);
            MatterStatusFrame frame = Assert.IsType<MatterStatusFrame>(result.Frame);
            Assert.Equal(commissioned, frame.Commissioned);
            Assert.Equal(expected, frame.Advertisement);
        }

        [Theory]
        [InlineData(true, "missing")]
        [InlineData(false, "notApplicable")]
        [InlineData(false, "unknown")]
        public void RejectsInconsistentOrUnknownStates(bool commissioned, string advertisement)
        {
            Assert.False(Parse(
                $$"""{"v":5,"type":"matterStatus","commissioned":{{commissioned.ToString().ToLowerInvariant()}},"advertisement":"{{advertisement}}"}""").Success);
        }

        [Fact]
        public void RejectsUnknownExtraKeys()
        {
            Assert.False(Parse(
                """{"v":5,"type":"matterStatus","commissioned":false,"advertisement":"visible","extra":true}""").Success);
        }
    }

    public sealed class TrustBoundary
    {
        [Fact]
        public void RejectsTrayOnlyFrameTypes()
        {
            SidecarParseResult state = Parse("""{"v":5,"type":"state","volume":40,"muted":false}""");
            Assert.False(state.Success);
            Assert.Contains("tray-only", state.Reason, StringComparison.Ordinal);

            SidecarParseResult ack = Parse($$"""{"v":5,"type":"ack","id":"{{Uuid1}}","ok":true}""");
            Assert.False(ack.Success);
            Assert.Contains("tray-only", ack.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAnUnknownFrameType()
        {
            SidecarParseResult result = Parse("""{"v":5,"type":"telemetry"}""");
            Assert.False(result.Success);
            Assert.Contains("telemetry", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownFrameTypeReasonStripsCrLfAndEscapeCharacters()
        {
            string type = "tele\r\n\u001bmetry";
            string json = JsonSerializer.Serialize(new { v = 5, type });

            SidecarParseResult result = Parse(json);

            Assert.False(result.Success);
            Assert.Contains("telemetry", result.Reason, StringComparison.Ordinal);
            Assert.All(result.Reason!, character => Assert.False(char.IsControl(character)));
        }

        [Theory]
        [InlineData("\"not a frame\"")]
        [InlineData("null")]
        [InlineData("42")]
        [InlineData("[]")]
        public void RejectsNonObjectInput(string json)
        {
            SidecarParseResult result = Parse(json);
            Assert.False(result.Success);
            Assert.Contains("object", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAnEmptyObject()
        {
            Assert.False(Parse("{}").Success);
        }

        [Fact]
        public void RejectsInvalidJsonWithAReason()
        {
            SidecarParseResult result = Parse("this is {{ not json");
            Assert.False(result.Success);
            Assert.Contains("JSON", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryRejectionSurfacesANonEmptyReason()
        {
            string[] invalid =
            [
                "not json",
                "null",
                "{}",
                """{"v":5,"type":"hello","token":"","protocol":1}""",
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"setVolume","value":101}""",
                $$"""{"v":5,"type":"action","id":"{{Uuid1}}","name":"custom","key":"Not A Slug","on":true}""",
                $$"""{"v":1,"type":"action","id":"{{Uuid1}}","name":"playPause"}""",
                """{"v":5,"type":"pairing","qrPayload":"nope","manualCode":"1"}""",
            ];
            foreach (string json in invalid)
            {
                SidecarParseResult result = Parse(json);
                Assert.False(result.Success);
                Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            }
        }
    }

    public sealed class TrayFrameSerialization
    {
        [Fact]
        public void SerializesAnOkAckWithNoErrorField()
        {
            string json = Protocol.Serialize(new AckOkFrame(Guid.Parse(Uuid1)));
            Assert.Equal($$"""{"v":5,"type":"ack","id":"{{Uuid1}}","ok":true}""", json);
        }

        [Fact]
        public void OkAcksCannotCarryAnErrorEvenByConstruction()
        {
            // The ok/fail split mirrors protocol.ts's discriminated union on
            // `ok`: AckOkFrame has no error member at all, so the invalid
            // "ok:true plus error" shape is unrepresentable. Assert the wire
            // form stays clean.
            string json = Protocol.Serialize(new AckOkFrame(Guid.Parse(Uuid1)));
            Assert.DoesNotContain("error", json, StringComparison.Ordinal);
        }

        [Fact]
        public void SerializesAFailedAckWithAnErrorString()
        {
            string json = Protocol.Serialize(new AckFailFrame(Guid.Parse(Uuid1), "socket disconnected"));
            Assert.Equal($$"""{"v":5,"type":"ack","id":"{{Uuid1}}","ok":false,"error":"socket disconnected"}""", json);
        }

        [Fact]
        public void OmitsErrorOnAFailedAckWithoutContext()
        {
            string json = Protocol.Serialize(new AckFailFrame(Guid.Parse(Uuid1)));
            Assert.Equal($$"""{"v":5,"type":"ack","id":"{{Uuid1}}","ok":false}""", json);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(40, false)]
        [InlineData(100, true)]
        public void SerializesStateFramesIncludingBoundaryVolumes(int volume, bool muted)
        {
            string json = Protocol.Serialize(new StateFrame(volume, muted));
            Assert.Equal($$"""{"v":5,"type":"state","volume":{{volume}},"muted":{{(muted ? "true" : "false")}}}""", json);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        public void StateFrameConstructionRejectsOutOfRangeVolume(int volume)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new StateFrame(volume, muted: false));
        }
    }
}
