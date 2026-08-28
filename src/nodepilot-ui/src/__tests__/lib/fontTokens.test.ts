import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * Drift guard for the app type system: IBM Plex Sans (UI) and IBM Plex Mono (code).
 *
 * An undeclared `--font-mono` makes Tailwind fall back to its own default silently, so the
 * font differs per operating system with no error at all. The CSS and TS sources are read
 * as text rather than imported: `__tests__/setup.ts` mocks `monacoSetup.ts` globally, so
 * importing it would assert against the mock value.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const uiSrc = join(__dirname, '..', '..');

const indexCss = readFileSync(join(uiSrc, 'index.css'), 'utf8');
const atelierCss = readFileSync(join(uiSrc, 'styles', 'designer-atelier.css'), 'utf8');
const monacoSetup = readFileSync(join(uiSrc, 'lib', 'monacoSetup.ts'), 'utf8');
const packageJson = JSON.parse(readFileSync(join(uiSrc, '..', 'package.json'), 'utf8'));

/**
 * Reads the value of a custom property declaration (`--foo: bar;`).
 * Usages (`var(--foo)`) and mentions in comments have no colon directly after the name,
 * so they do not match and cannot make a token look declared.
 */
function declaredValue(css: string, varName: string): string | null {
  const match = new RegExp(`${varName}\\s*:\\s*([^;]+);`).exec(css);
  return match ? match[1].trim() : null;
}

describe('font tokens', () => {
  it('declares every sans token as IBM Plex Sans', () => {
    for (const token of ['--font-headline', '--font-body', '--font-label']) {
      const value = declaredValue(indexCss, token);
      expect(value, `${token} must be declared in index.css`).not.toBeNull();
      expect(value).toContain("'IBM Plex Sans Variable'");
      // Generic fallback at the end, so the browser has a defined choice when the font
      // is missing (css:S4649).
      expect(value).toMatch(/sans-serif$/);
    }
  });

  it('declares --font-mono, the token that used to be missing entirely', () => {
    const value = declaredValue(indexCss, '--font-mono');
    expect(value, '--font-mono must be declared in index.css @theme').not.toBeNull();
    expect(value).toContain("'IBM Plex Mono'");
    expect(value).toMatch(/monospace$/);
  });

  it('keeps the Monaco constant in sync with --font-mono', () => {
    // Monaco measures character widths in JS and sanitizes the fontFamily string, so it
    // cannot resolve a CSS variable. The stack is therefore duplicated as a TS constant,
    // and this assertion keeps both copies equal.
    const match = /export const MONO_FONT_STACK\s*=\s*"([^"]+)"/.exec(monacoSetup);
    expect(match, 'MONO_FONT_STACK must be exported from lib/monacoSetup.ts').not.toBeNull();
    expect(match![1]).toBe(declaredValue(indexCss, '--font-mono'));
  });

  it('lets the Atelier skin follow the app mono stack instead of duplicating it', () => {
    expect(declaredValue(atelierCss, '--wd-mono')).toBe('var(--font-mono)');
  });

  it('self-hosts both families and drops the previous ones', () => {
    // The production CSP (SecurityPipelineSetup.cs) declares no `font-src` and falls back
    // to `default-src 'self'`, so a CDN import would be blocked in production.
    expect(indexCss).toContain("@import '@fontsource-variable/ibm-plex-sans';");
    expect(indexCss).toContain("@import '@fontsource/ibm-plex-mono/400.css';");
    expect(indexCss).toContain("@import '@fontsource/ibm-plex-mono/600.css';");
    expect(indexCss).not.toMatch(/@import\s+'@fontsource[^']*\/(inter|geist)/);
    expect(indexCss).not.toContain('Inter Variable');

    const deps = packageJson.dependencies as Record<string, string>;
    expect(deps).toHaveProperty('@fontsource-variable/ibm-plex-sans');
    expect(deps).toHaveProperty('@fontsource/ibm-plex-mono');
    expect(deps).not.toHaveProperty('@fontsource-variable/inter');
    // Geist is declared but never imported here. Only the docs site uses it, and it
    // carries its own dependency.
    expect(deps).not.toHaveProperty('@fontsource-variable/geist');
  });

  it('compensates the smaller x-height on body but not on monospace', () => {
    // Plex Sans has a small x-height, which shows in a UI built almost entirely on
    // text-xs/text-sm, so body text compensates. Code is excluded to keep columns aligned.
    expect(/font-size-adjust:\s*0?\.\d+/.test(indexCss)).toBe(true);
    expect(indexCss).toMatch(/\.font-mono\s*\{\s*\n?\s*font-size-adjust:\s*none;/);
  });
});
