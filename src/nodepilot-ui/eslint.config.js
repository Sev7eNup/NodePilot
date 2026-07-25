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
      // eslint-plugin-react-hooks 7.1 promoted three more React Compiler diagnostics to
      // errors. All six hits in this codebase were reviewed and none is a runtime defect —
      // they report what the compiler could not optimize, not incorrect code:
      //   refs                        — EdgeReshapeHandles.tsx:132, WorkflowEditorPage.tsx:1488.
      //                                 Ref writes inside pointer/context-menu callbacks that
      //                                 the compiler attributes to render scope because the
      //                                 handler is declared inline in JSX.
      //   preserve-manual-memoization — SubWorkflowPreviewModal.tsx:53, WorkflowEditorPage.tsx:927.
      //                                 "Existing memoization could not be preserved" on a
      //                                 hand-written useMemo/useCallback.
      //   use-memo                    — useNodeAnnotations.ts:222. Rejects the deliberate
      //                                 computed dependency key that already carries an
      //                                 exhaustive-deps disable.
      // Same rationale as set-state-in-effect above. Revisit if these sites are refactored.
      'react-hooks/refs': 'off',
      'react-hooks/preserve-manual-memoization': 'off',
      'react-hooks/use-memo': 'off',
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
