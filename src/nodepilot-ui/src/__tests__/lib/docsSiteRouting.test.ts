import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * Drift guard for the route into the documentation site, which is a second app.
 *
 * In production the API serves it from `wwwroot/docs` at `/docs`. In development nothing served
 * that path, so `/docs/` fell through to this SPA's index.html and the data router answered with
 * its not-found screen — the header's help button led straight into an error page.
 *
 * Three pieces have to agree for the link to work, and none of them fails loudly on its own:
 * the link is a real navigation, the app dev server proxies `/docs` to the docs dev server, and
 * the docs dev server serves under `/docs/` so its absolute entry-module URL stays inside the
 * proxied prefix. Vite honours that base on the command line but not from the config file.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const uiRoot = join(__dirname, '..', '..', '..');
const docsRoot = join(uiRoot, '..', 'nodepilot-docs-ui');

const viteConfig = readFileSync(join(uiRoot, 'vite.config.ts'), 'utf8');
const topBar = readFileSync(join(uiRoot, 'src', 'components', 'layout', 'TopBar.tsx'), 'utf8');
const docsPackageJson = JSON.parse(readFileSync(join(docsRoot, 'package.json'), 'utf8'));
const docsViteConfig = readFileSync(join(docsRoot, 'vite.config.ts'), 'utf8');

describe('documentation site routing', () => {
  it('links to /docs/ with a plain anchor, not a router Link', () => {
    // A `Link` would keep this SPA mounted and push /docs into its own history, so the request
    // would never reach the server that holds the documentation.
    expect(topBar).toContain('href="/docs/"');
    expect(topBar).not.toMatch(/<Link[^>]*to=["']\/docs/);
  });

  it('keeps the trailing slash, which the docs bundle resolves its assets against', () => {
    expect(topBar).toContain('href="/docs/"');
    expect(topBar).not.toContain('href="/docs"');
  });

  it('proxies /docs to the docs dev server so development matches production', () => {
    expect(viteConfig).toMatch(/'\/docs':\s*'http:\/\/localhost:5174'/);
  });

  it('does not rewrite the /docs prefix away', () => {
    // With the prefix stripped, the docs index.html would ask for /src/main.tsx at the app dev
    // server's root and boot this SPA instead of the documentation.
    const docsProxyBlock = viteConfig.slice(viteConfig.indexOf("'/docs'"));
    expect(docsProxyBlock).not.toContain('rewrite');
  });

  it('serves the docs dev server under /docs/ via the dev script', () => {
    expect(docsPackageJson.scripts.dev).toContain('--base=/docs/');
  });

  it('leaves the built docs base relative for wwwroot/docs and GitHub Pages', () => {
    expect(docsViteConfig).toMatch(/base:\s*'\.\/'/);
  });

  it('agrees on the docs dev server port', () => {
    expect(docsViteConfig).toMatch(/port:\s*5174/);
    expect(viteConfig).toContain('localhost:5174');
  });
});
