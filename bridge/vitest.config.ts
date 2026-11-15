import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    // matter.js 0.18 endpoint validation must fail construction tests rather
    // than merely log its production-default warnings.
    env: { MATTER_ENDPOINT_VALIDATION: "strict" },
    include: ["src/**/*.test.ts"],
    coverage: {
      provider: "v8",
      reporter: ["text", "lcov", "json-summary"],
      include: ["src/**/*.ts"],
      exclude: ["src/**/*.test.ts"],
      // ADR-006 §3 / ENGINEERING-STANDARDS: pure logic (mapping/, protocol,
      // config) must clear 90% lines per file; the rest is reported but not
      // gated here.
      thresholds: {
        "src/mapping/**": { lines: 90, perFile: true },
        "src/ipc/protocol.ts": { lines: 90 },
        "src/config.ts": { lines: 90 },
        // S5-R F2: the S5-1 pure modules are held to the same bar.
        "src/timing.ts": { lines: 90 },
        "src/matter/diagnostics.ts": { lines: 90 },
      },
    },
  },
});
