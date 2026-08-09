/**
 * Specification tests for the IPC protocol (docs/BLUEPRINT.md §2.3 as
 * amended by ADR-004: protocol v2, `custom` action variant).
 *
 * Each `describe` block documents one frame kind; test names describe the
 * accepted/rejected behaviour, not the schema/method under test.
 */
import { describe, expect, it } from "vitest";

import {
  ActionFrameSchema,
  AckFrameSchema,
  CUSTOM_KEY_MAX_LENGTH,
  CustomCommandKeySchema,
  HelloFrameSchema,
  PairingFrameSchema,
  PROTOCOL_VERSION,
  SidecarFrameSchema,
  StateFrameSchema,
  TrayFrameSchema,
  parseSidecarFrame,
  parseTrayFrame,
} from "./protocol.js";

const uuid1 = "123e4567-e89b-12d3-a456-426614174000";
const uuid2 = "00000000-0000-4000-8000-000000000000";

describe("PROTOCOL_VERSION", () => {
  it("is 2 (ADR-004: the additive custom action bumped v from 1)", () => {
    expect(PROTOCOL_VERSION).toBe(2);
  });
});

describe("hello frame", () => {
  const valid = { v: 2, type: "hello", token: "session-token", protocol: 1 };

  it("accepts a well-formed hello frame", () => {
    expect(HelloFrameSchema.safeParse(valid).success).toBe(true);
  });

  it("rejects an empty token", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, token: "" }).success).toBe(false);
  });

  it("rejects a missing v field", () => {
    const rest = { type: "hello", token: "session-token", protocol: 1 };
    expect(HelloFrameSchema.safeParse(rest).success).toBe(false);
  });

  it("rejects the superseded v:1 revision (only the current v is accepted)", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, v: 1 }).success).toBe(false);
  });

  it("rejects a v field from a future revision", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, v: 3 }).success).toBe(false);
  });

  it("rejects the wrong type discriminator", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, type: "action" }).success).toBe(false);
  });

  it("keeps protocol at literal 1 (v2 was additive, not breaking — ADR-004)", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, protocol: 2 }).success).toBe(false);
  });

  it("rejects unknown extra keys (strict boundary)", () => {
    expect(HelloFrameSchema.safeParse({ ...valid, extra: "nope" }).success).toBe(false);
  });
});

describe("action frame — bare actions (no payload)", () => {
  const bareNames = ["playPause", "next", "previous", "powerOn", "powerOff"] as const;

  it.each(bareNames)("accepts a well-formed %s action", (name) => {
    const frame = { v: 2, type: "action", id: uuid1, name };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it.each(bareNames)("rejects %s carrying an unexpected value field", (name) => {
    const frame = { v: 2, type: "action", id: uuid1, name, value: 1 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects an unknown action name", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "rewind" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a malformed uuid in id", () => {
    const frame = { v: 2, type: "action", id: "not-a-uuid", name: "playPause" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a missing id", () => {
    const frame = { v: 2, type: "action", name: "playPause" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects the superseded v:1 revision", () => {
    const frame = { v: 1, type: "action", id: uuid1, name: "playPause" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects unknown extra keys (strict boundary)", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "playPause", extra: "nope" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });
});

describe("action frame — setVolume", () => {
  it("accepts a well-formed setVolume action", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: 40 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("accepts the boundary value 0", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: 0 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("accepts the boundary value 100", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: 100 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("rejects a missing value field", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects volume below 0", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: -1 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects volume above 100", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: 101 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a fractional volume", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: 100.5 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a string volume", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setVolume", value: "40" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });
});

describe("action frame — setMuted", () => {
  it("accepts a well-formed setMuted action (true)", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setMuted", value: true };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("accepts a well-formed setMuted action (false)", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setMuted", value: false };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("rejects a missing value field", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setMuted" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a non-boolean value (string)", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setMuted", value: "true" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a non-boolean value (0/1 number)", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "setMuted", value: 1 };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });
});

describe("action frame — custom (ADR-004 §3)", () => {
  const valid = { v: 2, type: "action", id: uuid1, name: "custom", key: "movie-mode" };

  it("accepts a well-formed custom action", () => {
    expect(ActionFrameSchema.safeParse(valid).success).toBe(true);
  });

  it("accepts a single-word key", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "kodi" }).success).toBe(true);
  });

  it("accepts a key with digits", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "scene-2" }).success).toBe(true);
  });

  it("accepts a key at the 64-character boundary", () => {
    const key = "k".repeat(CUSTOM_KEY_MAX_LENGTH);
    expect(ActionFrameSchema.safeParse({ ...valid, key }).success).toBe(true);
  });

  it("rejects a key longer than 64 characters", () => {
    const key = "k".repeat(CUSTOM_KEY_MAX_LENGTH + 1);
    expect(ActionFrameSchema.safeParse({ ...valid, key }).success).toBe(false);
  });

  it("rejects a missing key field", () => {
    const frame = { v: 2, type: "action", id: uuid1, name: "custom" };
    expect(ActionFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects an empty key", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "" }).success).toBe(false);
  });

  it("rejects uppercase characters in the key", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "Movie-Mode" }).success).toBe(false);
  });

  it("rejects non-slug characters (underscore)", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "movie_mode" }).success).toBe(false);
  });

  it("rejects non-slug characters (space)", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "movie mode" }).success).toBe(false);
  });

  it("rejects a leading hyphen", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "-movie" }).success).toBe(false);
  });

  it("rejects a trailing hyphen", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "movie-" }).success).toBe(false);
  });

  it("rejects consecutive hyphens", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: "movie--mode" }).success).toBe(false);
  });

  it("rejects a non-string key", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, key: 42 }).success).toBe(false);
  });

  it("rejects a stray value field (custom carries no payload)", () => {
    expect(ActionFrameSchema.safeParse({ ...valid, value: 1 }).success).toBe(false);
  });
});

describe("CustomCommandKeySchema (shared env/wire slug rule)", () => {
  it("accepts a kebab-case slug", () => {
    expect(CustomCommandKeySchema.safeParse("stop-media").success).toBe(true);
  });

  it("rejects a non-slug string", () => {
    expect(CustomCommandKeySchema.safeParse("Stop Media!").success).toBe(false);
  });

  it("caps length at CUSTOM_KEY_MAX_LENGTH", () => {
    expect(CustomCommandKeySchema.safeParse("a".repeat(CUSTOM_KEY_MAX_LENGTH)).success).toBe(true);
    expect(CustomCommandKeySchema.safeParse("a".repeat(CUSTOM_KEY_MAX_LENGTH + 1)).success).toBe(
      false,
    );
  });
});

describe("pairing frame", () => {
  const valid = {
    v: 2,
    type: "pairing",
    qrPayload: "MT:Y.K9042C00KA0648G00",
    manualCode: "3497-011-2332",
  };

  it("accepts a well-formed pairing frame", () => {
    expect(PairingFrameSchema.safeParse(valid).success).toBe(true);
  });

  it("rejects a qrPayload missing the MT: prefix", () => {
    expect(
      PairingFrameSchema.safeParse({ ...valid, qrPayload: "Y.K9042C00KA0648G00" }).success,
    ).toBe(false);
  });

  it("rejects a qrPayload with a lowercase mt: prefix", () => {
    expect(
      PairingFrameSchema.safeParse({ ...valid, qrPayload: "mt:Y.K9042C00KA0648G00" }).success,
    ).toBe(false);
  });

  it("rejects an empty manualCode", () => {
    expect(PairingFrameSchema.safeParse({ ...valid, manualCode: "" }).success).toBe(false);
  });

  it("rejects a missing qrPayload", () => {
    const rest = { v: 2, type: "pairing", manualCode: "3497-011-2332" };
    expect(PairingFrameSchema.safeParse(rest).success).toBe(false);
  });

  it("rejects the superseded v:1 revision", () => {
    expect(PairingFrameSchema.safeParse({ ...valid, v: 1 }).success).toBe(false);
  });

  it("rejects the wrong type discriminator", () => {
    expect(PairingFrameSchema.safeParse({ ...valid, type: "hello" }).success).toBe(false);
  });

  it("rejects unknown extra keys (strict boundary)", () => {
    expect(PairingFrameSchema.safeParse({ ...valid, extra: "nope" }).success).toBe(false);
  });
});

describe("ack frame", () => {
  it("accepts a well-formed ok:true ack with no error field", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: true };
    expect(AckFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("rejects ok:true carrying an error field", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: true, error: "should not be here" };
    expect(AckFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("accepts a well-formed ok:false ack with an error string", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: false, error: "socket disconnected" };
    expect(AckFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("accepts ok:false with no error field (error is optional)", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: false };
    expect(AckFrameSchema.safeParse(frame).success).toBe(true);
  });

  it("rejects a non-boolean ok field", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: "true" };
    expect(AckFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects a malformed uuid in id", () => {
    const frame = { v: 2, type: "ack", id: "not-a-uuid", ok: true };
    expect(AckFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects the superseded v:1 revision", () => {
    const frame = { v: 1, type: "ack", id: uuid1, ok: true };
    expect(AckFrameSchema.safeParse(frame).success).toBe(false);
  });

  it("rejects unknown extra keys (strict boundary)", () => {
    const frame = { v: 2, type: "ack", id: uuid1, ok: true, extra: "nope" };
    expect(AckFrameSchema.safeParse(frame).success).toBe(false);
  });
});

describe("state frame", () => {
  const valid = { v: 2, type: "state", volume: 40, muted: false };

  it("accepts a well-formed state frame", () => {
    expect(StateFrameSchema.safeParse(valid).success).toBe(true);
  });

  it("accepts the boundary volume 0", () => {
    expect(StateFrameSchema.safeParse({ ...valid, volume: 0 }).success).toBe(true);
  });

  it("accepts the boundary volume 100", () => {
    expect(StateFrameSchema.safeParse({ ...valid, volume: 100 }).success).toBe(true);
  });

  it("rejects volume below 0", () => {
    expect(StateFrameSchema.safeParse({ ...valid, volume: -1 }).success).toBe(false);
  });

  it("rejects volume above 100", () => {
    expect(StateFrameSchema.safeParse({ ...valid, volume: 101 }).success).toBe(false);
  });

  it("rejects a fractional volume", () => {
    expect(StateFrameSchema.safeParse({ ...valid, volume: 100.5 }).success).toBe(false);
  });

  it("rejects a non-boolean muted field", () => {
    expect(StateFrameSchema.safeParse({ ...valid, muted: "false" }).success).toBe(false);
  });

  it("rejects the superseded v:1 revision", () => {
    expect(StateFrameSchema.safeParse({ ...valid, v: 1 }).success).toBe(false);
  });

  it("rejects unknown extra keys (strict boundary)", () => {
    expect(StateFrameSchema.safeParse({ ...valid, extra: "nope" }).success).toBe(false);
  });
});

describe("parseSidecarFrame (trust boundary, sidecar -> tray)", () => {
  it("accepts a hello frame", () => {
    const result = parseSidecarFrame({ v: 2, type: "hello", token: "t", protocol: 1 });
    expect(result.success).toBe(true);
  });

  it("accepts an action frame and narrows its payload type", () => {
    const result = parseSidecarFrame({
      v: 2,
      type: "action",
      id: uuid1,
      name: "setVolume",
      value: 40,
    });
    expect(result.success).toBe(true);
    if (result.success && result.data.type === "action" && result.data.name === "setVolume") {
      expect(result.data.value).toBe(40);
    } else {
      expect.fail("expected a parsed setVolume action frame");
    }
  });

  it("accepts a custom action frame and narrows its key", () => {
    const result = parseSidecarFrame({
      v: 2,
      type: "action",
      id: uuid1,
      name: "custom",
      key: "movie-mode",
    });
    expect(result.success).toBe(true);
    if (result.success && result.data.type === "action" && result.data.name === "custom") {
      expect(result.data.key).toBe("movie-mode");
    } else {
      expect.fail("expected a parsed custom action frame");
    }
  });

  it("accepts a pairing frame", () => {
    const result = parseSidecarFrame({
      v: 2,
      type: "pairing",
      qrPayload: "MT:ABC",
      manualCode: "1234-567-8901",
    });
    expect(result.success).toBe(true);
  });

  it("rejects a tray-only frame type (state)", () => {
    const result = parseSidecarFrame({ v: 2, type: "state", volume: 40, muted: false });
    expect(result.success).toBe(false);
  });

  it("rejects a non-object input", () => {
    expect(parseSidecarFrame("not a frame").success).toBe(false);
  });

  it("rejects null", () => {
    expect(parseSidecarFrame(null).success).toBe(false);
  });

  it("rejects an empty object", () => {
    expect(parseSidecarFrame({}).success).toBe(false);
  });
});

describe("parseTrayFrame (trust boundary, tray -> sidecar)", () => {
  it("accepts an ack frame", () => {
    const result = parseTrayFrame({ v: 2, type: "ack", id: uuid2, ok: true });
    expect(result.success).toBe(true);
  });

  it("accepts a state frame", () => {
    const result = parseTrayFrame({ v: 2, type: "state", volume: 12, muted: true });
    expect(result.success).toBe(true);
  });

  it("rejects a sidecar-only frame type (hello)", () => {
    const result = parseTrayFrame({ v: 2, type: "hello", token: "t", protocol: 1 });
    expect(result.success).toBe(false);
  });

  it("rejects a non-object input", () => {
    expect(parseTrayFrame(42).success).toBe(false);
  });

  it("rejects undefined", () => {
    expect(parseTrayFrame(undefined).success).toBe(false);
  });
});

describe("SidecarFrameSchema / TrayFrameSchema (direct schema symmetry)", () => {
  it("SidecarFrameSchema rejects frames only valid on the tray side", () => {
    expect(SidecarFrameSchema.safeParse({ v: 2, type: "ack", id: uuid1, ok: true }).success).toBe(
      false,
    );
  });

  it("TrayFrameSchema rejects frames only valid on the sidecar side", () => {
    expect(
      TrayFrameSchema.safeParse({ v: 2, type: "action", id: uuid1, name: "playPause" }).success,
    ).toBe(false);
  });
});
