import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { SECTION_GLOW_COLOR } from '../../../../components/designer/header/sectionColors';

/**
 * Each toolbar section glows in a color that must be a `var(--token)` referencing an
 * index.css custom property declared in both light (:root / @theme) and dark (html.dark)
 * mode. A theme without the declaration renders an empty color-mix and an invisible bloom.
 * Going up four levels from src/__tests__/components/designer/header/ lands at src/index.css.
 */
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const cssText = readFileSync(join(__dirname, '..', '..', '..', '..', 'index.css'), 'utf8');

/**
 * Classify each top-level CSS block as light (`:root` / `@theme`) or dark (any selector
 * containing `html.dark`), then report how many of each declare `varName`. A token may be
 * re-declared under several dark scopes (base `html.dark` plus the `html.dark .np-shell`
 * app-shell override), so a raw occurrence count is the wrong signal; what matters is that
 * the token is present in both a light and a dark scope.
 */
function declarationScopes(css: string, varName: string): { light: number; dark: number } {
  const escaped = varName.replace(/[-\\]/g, '\\$&');
  // `--foo:` (declaration). `var(--foo)` call-sites end in `)`, and `--foo-bar:` keeps the
  // `-`, so neither matches `--foo\s*:`.
  const declRe = new RegExp(`${escaped}\\s*:`);
  let light = 0;
  let dark = 0;
  let depth = 0;
  let blockStart = 0;
  let selector = '';
  for (let i = 0; i < css.length; i++) {
    const ch = css[i];
    if (ch === '{') {
      if (depth === 0) {
        selector = css.slice(blockStart, i);
        blockStart = i + 1;
      }
      depth++;
    } else if (ch === '}') {
      depth--;
      if (depth === 0) {
        if (declRe.test(css.slice(blockStart, i))) {
          if (/html\.dark/.test(selector)) dark++;
          else light++;
        }
        blockStart = i + 1;
      }
    }
  }
  return { light, dark };
}

describe('Toolbar section glow colors', () => {
  for (const [id, value] of Object.entries(SECTION_GLOW_COLOR)) {
    it(`"${id}" references a theme token declared in both light and dark mode`, () => {
      const match = value.match(/var\((--[a-z0-9-]+)\)/i);
      expect(match, `Section "${id}" color "${value}" must be a var(--token) reference`).toBeTruthy();

      const varName = match![1];
      const { light, dark } = declarationScopes(cssText, varName);
      expect(
        light >= 1 && dark >= 1,
        `Expected ${varName} (section "${id}") to be declared in BOTH a light scope ` +
          `(:root / @theme) and a dark scope (html.dark…) in index.css — found ${light} light / ` +
          `${dark} dark. A theme with no declaration renders an empty color-mix → invisible bloom.`,
      ).toBe(true);
    });
  }
});
