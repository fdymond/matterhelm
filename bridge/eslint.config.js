// @ts-check
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import eslintConfigPrettier from "eslint-config-prettier";
import { createTypeScriptImportResolver } from "eslint-import-resolver-typescript";
import importX from "eslint-plugin-import-x";
import tseslint from "typescript-eslint";

export default defineConfig(
  globalIgnores(["dist/**", "coverage/**"]),
  js.configs.recommended,
  tseslint.configs.strictTypeChecked,
  tseslint.configs.stylisticTypeChecked,
  {
    plugins: { "import-x": importX },
    languageOptions: {
      parserOptions: {
        // Config files at the repo root (this file, vitest.config.ts) live
        // outside tsconfig.json's `include: ["src"]`; allowDefaultProject
        // lets them still be linted without a dedicated tsconfig.
        projectService: {
          allowDefaultProject: ["eslint.config.js", "vitest.config.ts"],
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    settings: {
      // S1-6: import-x/no-restricted-paths resolves each import specifier to
      // an absolute file before checking it against a zone. Source imports
      // use NodeNext-style specifiers (e.g. "../ipc/protocol.js") pointing at
      // .ts files on disk; the default resolver can't bridge that .js/.ts gap
      // and silently skips unresolved imports (rule never fires — verified:
      // without this, a matter/ -> ipc/ violation lints clean). This resolver
      // understands the tsconfig "paths"/extension mapping, so it is required
      // for the boundary rule below to actually work, not just for ordering.
      "import-x/resolver-next": [createTypeScriptImportResolver({ alwaysTryTypes: true })],
    },
    rules: {
      "no-console": "error",
      "import-x/order": [
        "error",
        {
          alphabetize: { order: "asc", caseInsensitive: true },
          "newlines-between": "always",
        },
      ],
      // matter/ <-> ipc/ is a one-way-forbidden pair on BOTH sides (CLAUDE.md
      // invariant): they only meet in index.ts via mapping/, which is legal
      // to import from — and be imported by — either side, so it is not a
      // zone here. S1-5's index.ts/config.ts/log.ts wiring lives outside both
      // zones and is unaffected.
      "import-x/no-restricted-paths": [
        "error",
        {
          zones: [
            {
              target: "./src/matter",
              from: "./src/ipc",
              message:
                "matter/ must not import ipc/ (CLAUDE.md invariant) — share plain data via mapping/ instead.",
            },
            {
              target: "./src/ipc",
              from: "./src/matter",
              message:
                "ipc/ must not import matter/ (CLAUDE.md invariant) — share plain data via mapping/ instead.",
            },
          ],
        },
      ],
    },
  },
  // The next three blocks all configure `no-restricted-imports`. Flat config
  // does not merge a rule's options across matching blocks — the LAST
  // matching block for a given file wins outright (verified: a naive
  // two-block split silently dropped the @matter restriction for every file
  // matched by both). So each block below targets a mutually exclusive file
  // set and states its own COMPLETE `no-restricted-imports` value for that
  // set, rather than relying on merge-by-file-set.
  {
    // Everything except adapter.ts (may use matter.js) and *.test.ts (may use
    // ws, the mock-WS-peer devDep): neither @matter/*/@project-chip/* nor ws
    // is legal here.
    files: ["src/**/*.ts"],
    ignores: ["src/matter/adapter.ts", "src/**/*.test.ts"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["@matter/**", "@project-chip/**"],
              message:
                "matter.js (@matter/*, @project-chip/*) imports are only allowed in src/matter/adapter.ts — add the wrapper there instead (docs/ENGINEERING-STANDARDS.md principle 1).",
            },
          ],
          paths: [
            {
              name: "ws",
              message:
                "ws is a test-only devDependency — src/ uses the global WebSocket, not this package.",
            },
          ],
        },
      ],
    },
  },
  {
    // adapter.ts is the one legal matter.js import site; it still must not
    // pull in the test-only ws package.
    files: ["src/matter/adapter.ts"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          paths: [
            {
              name: "ws",
              message:
                "ws is a test-only devDependency — src/ uses the global WebSocket, not this package.",
            },
          ],
        },
      ],
    },
  },
  {
    // Tests may use ws (mock WS peer) but must still reach matter.js only
    // through matter/adapter.ts, never directly.
    files: ["src/**/*.test.ts"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["@matter/**", "@project-chip/**"],
              message:
                "matter.js (@matter/*, @project-chip/*) imports are only allowed in src/matter/adapter.ts — add the wrapper there instead (docs/ENGINEERING-STANDARDS.md principle 1).",
            },
          ],
        },
      ],
    },
  },
  eslintConfigPrettier,
);
