import { defineConfig } from 'vitest/config'
import { PAGES_ORIGIN, siteOrigin } from './scripts/site-origin.mjs'

/**
 * Node-environment unit tests for the pure logic behind the bilingual site: language
 * detection, the language/path split the router runs on every navigation, and the content
 * lookup with its fallback. Rendering is out of scope: the components are thin wrappers
 * over these functions, and the docs site has no component-test harness.
 */
export default defineConfig({
  // The same constants vite.config.ts defines. Without them `lib/content.ts` throws a
  // ReferenceError the moment a test imports it.
  define: {
    __NP_PAGES_ORIGIN__: JSON.stringify(PAGES_ORIGIN),
    __NP_SITE_ORIGIN__: JSON.stringify(siteOrigin()),
  },
  test: {
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.{ts,mjs}'],
  },
})
