import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  // coverage/ holds generated istanbul HTML with its own eslint-disable banners. It exists
  // only locally, so linting it just makes the local warning count differ from CI.
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
      // Several component modules deliberately export shared constants and helpers. Fast
      // Refresh still works, and enforcing this rule would only force wide file moves.
      'react-refresh/only-export-components': 'off',
      // The React compiler lint flags common synchronous state-derivation effects as errors.
      // Disabling just this rule keeps the rest of react-hooks recommended active without the
      // false positives in the existing editor controls.
      'react-hooks/set-state-in-effect': 'off',
      // An underscore prefix is the project convention for an intentionally unused binding:
      // arguments kept for a stable callback signature, discarded destructure elements, and
      // so on. Matching the standard tseslint pattern avoids scattered eslint-disable lines.
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
  // All user-visible date, time and number formatting goes through lib/format.ts, whose helpers
  // resolve the locale from the active i18n language. A direct toLocale*() call hardcodes or
  // omits the locale and ignores the UI language switch. format.ts is the one place allowed to
  // call the primitives; tests may use them to build expected values that stay independent of
  // the runtime locale.
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
