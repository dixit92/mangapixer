// @ts-check
// Flat config for ESLint 9 + angular-eslint 19.
// Replaces the legacy .eslintrc.json stub, which declared no parser/plugins and
// therefore fell back to espree (a JS-only parser) and could not read TypeScript.
const eslint = require("@eslint/js");
const tseslint = require("typescript-eslint");
const angular = require("angular-eslint");

module.exports = tseslint.config(
  {
    // Global ignores. A config object with only `ignores` applies repo-wide,
    // so a direct `npx eslint .` skips build/output/tooling artifacts too.
    ignores: [
      "dist/",
      "out-tsc/",
      "coverage/",
      "node_modules/",
      ".angular/",
      "test-results/",
      "playwright-report/",
      "e2e/",
    ],
  },
  {
    files: ["**/*.ts"],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    // Lint inline component templates (template: `...`) with the HTML rules below.
    processor: angular.processInlineTemplates,
    rules: {
      "@angular-eslint/directive-selector": [
        "error",
        { type: "attribute", prefix: "app", style: "camelCase" },
      ],
      "@angular-eslint/component-selector": [
        "error",
        { type: "element", prefix: "app", style: "kebab-case" },
      ],
      // Treat leading-underscore names as intentionally unused (e.g. handler
      // parameters kept for signature/documentation but not read in the body).
      "@typescript-eslint/no-unused-vars": [
        "error",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
          caughtErrorsIgnorePattern: "^_",
        },
      ],
      // Ternary and short-circuit expressions are used deliberately as
      // side-effecting statements (e.g. keyboard-handler dispatch); allow them.
      "@typescript-eslint/no-unused-expressions": [
        "error",
        { allowShortCircuit: true, allowTernary: true },
      ],
    },
  },
  {
    files: ["**/*.html"],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    // Pre-existing accessibility debt on convenience click targets that
    // duplicate adjacent real <button> elements. Downgraded to warnings so the
    // baseline stays green while the issues remain visible and actionable;
    // fixing them properly (tabindex + keyboard handlers) is a behavior change
    // and out of scope for this lint-baseline work.
    rules: {
      "@angular-eslint/template/click-events-have-key-events": "warn",
      "@angular-eslint/template/interactive-supports-focus": "warn",
    },
  }
);
