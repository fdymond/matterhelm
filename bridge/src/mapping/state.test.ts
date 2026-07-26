/**
 * Specification tests for mapping/state.ts (docs/BLUEPRINT.md §2.2).
 *
 * Test names describe the mapping behaviour, not the function under test.
 */
import { describe, expect, it } from "vitest";

import { levelToVolume } from "./actions.js";
import { mutedToOnOff, stateFrameToSpeakerAttributes, volumeToLevel } from "./state.js";

describe("volumeToLevel (0-100% -> 0-254, round-to-nearest)", () => {
  it("maps the minimum volume 0% to level 0", () => {
    expect(volumeToLevel(0)).toBe(0);
  });

  it("maps the maximum volume 100% to level 254", () => {
    expect(volumeToLevel(100)).toBe(254);
  });

  it("maps the exact midpoint 50% to level 127", () => {
    expect(volumeToLevel(50)).toBe(127);
  });

  it("rounds volume 1% up to level 3 (2.54 rounds to 3)", () => {
    expect(volumeToLevel(1)).toBe(3);
  });

  it("rounds volume 99% down to level 251 (251.46 rounds to 251)", () => {
    expect(volumeToLevel(99)).toBe(251);
  });

  it("clamps a negative volume to level 0", () => {
    expect(volumeToLevel(-5)).toBe(0);
  });

  it("clamps a volume above 100 to level 254", () => {
    expect(volumeToLevel(500)).toBe(254);
  });
});

describe("mutedToOnOff (Speaker OnOff polarity: muted = Off, unmuted = On)", () => {
  it("maps muted (true) to Off (false)", () => {
    expect(mutedToOnOff(true)).toBe(false);
  });

  it("maps unmuted (false) to On (true)", () => {
    expect(mutedToOnOff(false)).toBe(true);
  });
});

describe("stateFrameToSpeakerAttributes", () => {
  it("maps volume to currentLevel and unmuted to On", () => {
    expect(stateFrameToSpeakerAttributes({ volume: 40, muted: false })).toEqual({
      currentLevel: volumeToLevel(40),
      onOff: true,
    });
  });

  it("maps muted to Off regardless of volume", () => {
    expect(stateFrameToSpeakerAttributes({ volume: 40, muted: true })).toEqual({
      currentLevel: volumeToLevel(40),
      onOff: false,
    });
  });

  it("maps the boundary volume 0 with muted false", () => {
    expect(stateFrameToSpeakerAttributes({ volume: 0, muted: false })).toEqual({
      currentLevel: 0,
      onOff: true,
    });
  });

  it("maps the boundary volume 100 with muted true", () => {
    expect(stateFrameToSpeakerAttributes({ volume: 100, muted: true })).toEqual({
      currentLevel: 254,
      onOff: false,
    });
  });
});

describe("volume <-> level round trip", () => {
  it("recovers every integer percent 0-100 after volumeToLevel then levelToVolume", () => {
    for (let volume = 0; volume <= 100; volume += 1) {
      const level = volumeToLevel(volume);
      expect(levelToVolume(level)).toBe(volume);
    }
  });
});
