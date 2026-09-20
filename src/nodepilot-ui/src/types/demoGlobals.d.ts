/**
 * Build-time flag, replaced by Vite's `define`. `false` in the product and in tests, `true`
 * only in the browser-demo build.
 *
 * It exists for one decision the product and the demo cannot share: path routing needs a
 * server that rewrites unknown paths, which a static host does not do.
 */
declare const __NP_DEMO__: boolean;

/**
 * Version the demo reports in its top bar, injected from package.json by vite.demo.config.ts.
 *
 * Declared here rather than beside the demo sources because the demo's own tests live under
 * `src/__tests__`, so the app's TypeScript project has to know the symbol too.
 */
declare const __NP_DEMO_VERSION__: string;
