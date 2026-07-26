import { defineConfig } from 'vitest/config';

/**
 * Node-environment unit tests for the pure logic in the Electron shell: the desktop.json
 * handoff validator and the certificate-pinning / navigation guards. Everything that needs a
 * live Electron runtime stays out of scope — those files take their Electron objects as
 * parameters precisely so the decision logic can be exercised here.
 */
export default defineConfig({
  test: {
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.ts'],
  },
});
