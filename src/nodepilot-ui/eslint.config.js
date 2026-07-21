import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist', 'playwright-report']),
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
])
