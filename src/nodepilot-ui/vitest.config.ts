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
      // Floors set ~2pp below the 2026-04-26 baseline (lines 33% / branches 24% /
      // statements 30% / functions 23%) so the gate fails on a step-change regression
      // but tolerates normal day-to-day churn. Raise these in lockstep as P1.6
      // property-config tests expand; never lower without a written reason in the PR.
      // 2026-05-08: tightened by +1pp across all four metrics (was 30/21/27/20). Next
      // increase wants a fresh measurement and ideally new tests in the designer-canvas
      // hot-path (WorkflowEditorPage, ExecutionPanel) — see docs/testing/e2e-tests.md.
      thresholds: {
        lines: 31,
        branches: 22,
        statements: 28,
        functions: 21,
      },
    },
  },
});
