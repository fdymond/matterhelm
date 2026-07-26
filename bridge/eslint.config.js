// @ts-check
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import eslintConfigPrettier from "eslint-config-prettier";
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
    rules: {
      "no-console": "error",
      // Ordering only — full path resolution (import-x/no-unresolved etc.)
      // needs eslint-import-resolver-typescript, which isn't justified by
      // any story yet; NodeNext + tsc already catch unresolved imports.
      "import-x/order": [
        "error",
        {
          alphabetize: { order: "asc", caseInsensitive: true },
          "newlines-between": "always",
        },
      ],
    },
  },
  eslintConfigPrettier,
);
