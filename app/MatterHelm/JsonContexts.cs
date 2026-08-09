using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatterHelm;

/// <summary>
/// <see cref="JsonStringEnumConverter{TEnum}"/> preconfigured for camelCase
/// member names, so it can be referenced from a source-gen options attribute
/// (which only takes parameterless converter types). Preserves the exact enum
/// wire form <c>Config</c> has always written ("pauseAndDisplaysOff").
/// </summary>
internal sealed class PowerOffActionJsonConverter() : JsonStringEnumConverter<PowerOffAction>(JsonNamingPolicy.CamelCase);

/// <summary>Same camelCase enum treatment for <see cref="MediaKeyName"/> ("stop", "volumeUp", …).</summary>
internal sealed class MediaKeyNameJsonConverter() : JsonStringEnumConverter<MediaKeyName>(JsonNamingPolicy.CamelCase);

/// <summary>Same camelCase enum treatment for <see cref="OverlayPosition"/> ("bottomCenter", "topRight", …).</summary>
internal sealed class OverlayPositionJsonConverter() : JsonStringEnumConverter<OverlayPosition>(JsonNamingPolicy.CamelCase);

/// <summary>
/// Source-generated serialization contract (ADR-005 §2) for the
/// <c>config.json</c> document shape: camelCase properties, indented output,
/// camelCase string enums — byte-for-byte the format the reflection-based
/// writer produced. Used by <see cref="Config"/>'s write path and
/// <c>SettingsViewModel</c>'s clone/snapshot round-trips. The strict inbound
/// trust boundaries (config load, IPC frames) deliberately keep their
/// hand-rolled parsing — see <see cref="Config"/> and <c>Sidecar/Protocol</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = new[] { typeof(PowerOffActionJsonConverter), typeof(MediaKeyNameJsonConverter), typeof(OverlayPositionJsonConverter) })]
[JsonSerializable(typeof(BridgeConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated contract for the compact (unindented) camelCase JSON the
/// tray app hands the sidecar via <c>HTPC_BRIDGE_ENDPOINTS</c> (ADR-004 §2);
/// see <c>BridgeHost.BuildSidecarExtraEnv</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SidecarEndpointsEnv))]
internal sealed partial class SidecarEnvJsonContext : JsonSerializerContext;
