import { defineConfig } from 'vitest/config'

/**
 * Node-environment unit tests for the pure logic behind the bilingual site: language
 * detection, the language/path split the router runs on every navigation, and the content
 * lookup with its fallback.
 *
 * Rendering is deliberately out of scope — the components are thin wrappers over these
 * functions, and the docs site has no component-test harness. What is tested here is what
 * silently breaks: a wrong split serves the wrong language, and a wrong fallback serves a
 * blank page.
 */
export default defineConfig({
  test: {
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.ts'],
  },
})
