import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/__tests__/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // Headroom for the heavy designer-page tests when coverage instrumentation slows the
    // runner. Must stay above the waitFor timeout (asyncUtilTimeout in setup.ts).
    testTimeout: 15000,
    // Absorb residual CI flake by re-running a failed test up to twice. A deterministic
    // failure loses every retry and still goes red. Locally retries stay off.
    retry: process.env.CI ? 2 : 0,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // Pure-render and config-glue files carry no logic and would skew the report.
      // Keep the list short: remove an entry once its files gain real branching.
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
      // Ratchet: floors sit just below the current measured coverage, so the gate fails on a
      // real regression while tolerating day-to-day churn. Only raise them, never lower.
      thresholds: {
        lines: 73,
        branches: 58,
        statements: 70,
        functions: 57,
      },
    },
  },
});
