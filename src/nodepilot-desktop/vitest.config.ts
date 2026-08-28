import { defineConfig } from 'vitest/config';

/**
 * Node-environment unit tests for the pure logic in the Electron shell: the desktop.json handoff
 * validator and the certificate-pinning and navigation guards. Code that needs a live Electron
 * runtime is out of scope; those files take their Electron objects as parameters so the decision
 * logic can be tested here.
 */
export default defineConfig({
  test: {
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.ts'],
  },
});
