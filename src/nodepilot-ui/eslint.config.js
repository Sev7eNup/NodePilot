import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  // coverage/ holds generated istanbul HTML (with its own eslint-disable banners). CI never
  // sees it because lint runs before test:coverage, so linting it only ever produced a
  // local-vs-CI discrepancy in the warning count.
  globalIgnores(['dist', 'playwright-report', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    rules: {
      // The project intentionally exports shared constants/helpers from several component
      // modules; Fast Refresh still works, and enforcing this rule would require broad file
      // moves unrelated to runtime correctness.
      'react-refresh/only-export-components': 'off',
      // Current React compiler lint treats common sync state-derivation effects as errors.
      // Keep the rest of react-hooks recommended rules active while avoiding noisy false
      // positives in existing editor controls.
      'react-hooks/set-state-in-effect': 'off',
      // Underscore-prefix is the project convention for "intentionally unused" — args kept
      // for stable callback signatures, destructure-discards, etc. Match the standard
      // tseslint pattern so we don't have to scatter eslint-disable comments.
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
        destructuredArrayIgnorePattern: '^_',
        caughtErrorsIgnorePattern: '^_',
      }],
    },
  },
  {
    files: ['src/**/*.test.{ts,tsx}', 'src/**/__tests__/**/*.{ts,tsx}'],
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
  // All user-visible date/time/number formatting must go through lib/format.ts: its helpers
  // resolve the locale from the active i18n language, while a direct toLocale*() call hardcodes
  // or omits the locale and silently ignores the UI language switch. format.ts itself is the
  // one place allowed to call the primitives; tests may build locale-dependent EXPECTED values
  // with toLocale*() to stay independent of the runtime locale.
  {
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/lib/format.ts', 'src/**/*.test.{ts,tsx}', 'src/**/__tests__/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-syntax': ['error', {
        selector: "MemberExpression[property.name=/^toLocale(String|DateString|TimeString)$/]",
        message: 'Do not call toLocaleString/toLocaleDateString/toLocaleTimeString directly — use the i18n-aware helpers in src/lib/format.ts (formatDate, formatDateOnly, formatTime, formatNumber) so output follows the UI language.',
      }],
    },
  },
])
