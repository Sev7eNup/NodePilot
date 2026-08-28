import { defineConfig } from 'vitest/config'

/**
 * Node-environment unit tests for the pure logic behind the bilingual site: language
 * detection, the language/path split the router runs on every navigation, and the content
 * lookup with its fallback. Rendering is out of scope: the components are thin wrappers
 * over these functions, and the docs site has no component-test harness.
 */
export default defineConfig({
  test: {
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.ts'],
  },
})
