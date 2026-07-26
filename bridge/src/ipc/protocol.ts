/**
 * IPC protocol between the bridge sidecar and the tray application.
 *
 * BLUEPRINT.md §2.3 is the normative spec; this module is its executable
 * form — one JSON object per WebSocket message, exchanged over the loopback
 * connection described there.
 *
 * Versioning rules (binding, see CLAUDE.md rule 5 for the escalation path):
 * - `v` is the per-message revision, currently {@link PROTOCOL_VERSION}, and
 *   is present on every frame. Additive, backwards-compatible evolution
 *   (a new optional field, a new frame variant) bumps `v`.
 * - Breaking changes (removing/renaming a field, changing a field's type or
 *   meaning) are NOT expressed via `v` — they bump `protocol` in the `hello`
 *   frame instead, and require an ADR in docs/adr/ before landing.
 *
 * This module is pure: no matter.js, no `ws`, no I/O. `parseSidecarFrame`
 * and `parseTrayFrame` are the trust boundary for their respective
 * directions — every inbound frame must be run through one of them before
 * any field is trusted, per docs/ENGINEERING-STANDARDS.md ("parse, don't
 * validate-and-hope").
 */
import { z } from "zod";

/** Current per-message revision. Bump additively only — see module doc. */
export const PROTOCOL_VERSION = 1;

/** Shared `v` field: every frame, both directions, carries this literal. */
const vField = z.literal(PROTOCOL_VERSION);

// ---------------------------------------------------------------------------
// Sidecar -> tray app
// ---------------------------------------------------------------------------

/**
 * First frame the sidecar must send; the tray app closes the socket on
 * anything else arriving first. `protocol` is the breaking-change counter
 * (see module doc), independent of the per-message `v`.
 */
export const HelloFrameSchema = z.strictObject({
  v: vField,
  type: z.literal("hello"),
  token: z.string().min(1),
  protocol: z.literal(1),
});
export type HelloFrame = z.infer<typeof HelloFrameSchema>;

/** Fields common to every `action` variant; each variant extends this. */
const actionFrameBase = z.object({
  v: vField,
  type: z.literal("action"),
  id: z.uuid(),
});

// Design decision: `action` is modelled as a discriminated union on `name`
// nested inside the outer discriminated union on `type` (zod v4 resolves
// nested discriminated unions natively). Each action variant is `.strict()`
// so a payload mismatch — `setVolume` missing `value`, or a bare action like
// `playPause` carrying a stray `value` — is a rejection, not a silent pass.
// This is deliberately the strict, spec-enforcing choice: the action union
// IS the contract for what each action name means.

const ActionPlayPauseSchema = actionFrameBase.extend({ name: z.literal("playPause") }).strict();
const ActionNextSchema = actionFrameBase.extend({ name: z.literal("next") }).strict();
const ActionPreviousSchema = actionFrameBase.extend({ name: z.literal("previous") }).strict();
const ActionPowerOnSchema = actionFrameBase.extend({ name: z.literal("powerOn") }).strict();
const ActionPowerOffSchema = actionFrameBase.extend({ name: z.literal("powerOff") }).strict();
/** `value` is an integer 0-100; matter.js LevelControl percent, not 0-254. */
const ActionSetVolumeSchema = actionFrameBase
  .extend({ name: z.literal("setVolume"), value: z.int().min(0).max(100) })
  .strict();
const ActionSetMutedSchema = actionFrameBase
  .extend({ name: z.literal("setMuted"), value: z.boolean() })
  .strict();

export const ActionFrameSchema = z.discriminatedUnion("name", [
  ActionPlayPauseSchema,
  ActionNextSchema,
  ActionPreviousSchema,
  ActionPowerOnSchema,
  ActionPowerOffSchema,
  ActionSetVolumeSchema,
  ActionSetMutedSchema,
]);
export type ActionFrame = z.infer<typeof ActionFrameSchema>;

// Design decision: enforce the "MT:" prefix on `qrPayload`. It isn't
// incidental formatting — it's the fixed Matter onboarding-payload prefix
// (per the Matter spec, mirrored by matter.js's own
// `pairingCodes.qrPairingCode`) — so a payload missing it is a bridge bug,
// not a valid-but-unusual value, and should fail loudly here rather than
// reach the tray app's QR renderer. `manualCode` is left as a non-empty
// string only: its digit-grouping/checksum format is matter.js's concern,
// not this protocol's, and validating it here would just duplicate (and
// risk drifting from) that library's own invariants — no story needs it.
export const PairingFrameSchema = z.strictObject({
  v: vField,
  type: z.literal("pairing"),
  qrPayload: z.string().startsWith("MT:"),
  manualCode: z.string().min(1),
});
export type PairingFrame = z.infer<typeof PairingFrameSchema>;

/** Union of every frame the sidecar sends to the tray app. */
export const SidecarFrameSchema = z.discriminatedUnion("type", [
  HelloFrameSchema,
  ActionFrameSchema,
  PairingFrameSchema,
]);
export type SidecarFrame = z.infer<typeof SidecarFrameSchema>;

// ---------------------------------------------------------------------------
// Tray app -> sidecar
// ---------------------------------------------------------------------------

// Design decision: `ack` is a discriminated union on `ok` rather than a
// single object with an always-optional `error`. When `ok: true` there is
// nothing to explain, so `error` is disallowed (a stray `error` alongside
// `ok: true` is a rejection); when `ok: false`, `error` is optional context,
// matching the blueprint's "plus optional error string when ok=false".
const AckOkSchema = z
  .object({
    v: vField,
    type: z.literal("ack"),
    id: z.uuid(),
    ok: z.literal(true),
  })
  .strict();
const AckFailSchema = z
  .object({
    v: vField,
    type: z.literal("ack"),
    id: z.uuid(),
    ok: z.literal(false),
    error: z.string().optional(),
  })
  .strict();

export const AckFrameSchema = z.discriminatedUnion("ok", [AckOkSchema, AckFailSchema]);
export type AckFrame = z.infer<typeof AckFrameSchema>;

/** Reported on connect and on every change (executor observes CoreAudio). */
export const StateFrameSchema = z.strictObject({
  v: vField,
  type: z.literal("state"),
  volume: z.int().min(0).max(100),
  muted: z.boolean(),
});
export type StateFrame = z.infer<typeof StateFrameSchema>;

/** Union of every frame the tray app sends to the sidecar. */
export const TrayFrameSchema = z.discriminatedUnion("type", [AckFrameSchema, StateFrameSchema]);
export type TrayFrame = z.infer<typeof TrayFrameSchema>;

// ---------------------------------------------------------------------------
// Trust boundary
// ---------------------------------------------------------------------------

/**
 * Parses an unknown value (freshly `JSON.parse`d from the socket) as a
 * sidecar->tray frame. This is the only place inbound sidecar frames should
 * be trusted from — never cast, always parse.
 */
export function parseSidecarFrame(input: unknown): z.ZodSafeParseResult<SidecarFrame> {
  return SidecarFrameSchema.safeParse(input);
}

/**
 * Parses an unknown value (freshly `JSON.parse`d from the socket) as a
 * tray->sidecar frame. This is the only place inbound tray frames should be
 * trusted from — never cast, always parse.
 */
export function parseTrayFrame(input: unknown): z.ZodSafeParseResult<TrayFrame> {
  return TrayFrameSchema.safeParse(input);
}
