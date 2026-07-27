import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/__tests__/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // Headroom for the heavy designer-page tests (WorkflowEditorPage.test.tsx: ~89 tests,
    // 144 async waits) once v8 coverage instrumentation slows the CI runner. The waitFor
    // default is 5000 ms (asyncUtilTimeout in setup.ts); the per-test budget must sit above it.
    testTimeout: 15000,
    // Belt-and-braces for residual CI flake: re-run a failed test up to twice in CI only.
    // A deterministic failure loses every retry and still goes red, so this hides no real
    // bug; locally retry stays 0 so developers see honest first-run results.
    retry: process.env.CI ? 2 : 0,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // Pure-render and config-glue files would skew the report — exclude them so the
      // headline number reflects code with actual logic. Keep this list short on purpose;
      // if a category becomes meaningful (e.g. icons that gain branching), drop it from
      // the exclude list rather than carrying dead lines forever.
      exclude: [
        'node_modules/**',
        'dist/**',
        'src/main.tsx',
        'src/vite-env.d.ts',
        'src/types/**',
        'src/**/*.d.ts',
        'src/__tests__/**',
        '**/*.test.{ts,tsx}',
        '**/*.spec.{ts,tsx}',
      ],
      // Ratchet: ~3pp below the measured value, so the gate fails on a real regression while
      // tolerating day-to-day churn. Never lower without a written reason in the PR.
      //
      // Re-measured 2026-07-27 (184 files / 2354 tests): lines 76.50, statements 73.71,
      // branches 62.01, functions 60.65. The previous floors (31/28/22/21) were the
      // 2026-04-26 baseline and had drifted ~45pp below reality — at that distance the gate
      // could not fail on anything short of deleting most of the suite, which is the one
      // thing a coverage gate exists to catch.
      thresholds: {
        lines: 73,
        branches: 58,
        statements: 70,
        functions: 57,
      },
    },
  },
});
