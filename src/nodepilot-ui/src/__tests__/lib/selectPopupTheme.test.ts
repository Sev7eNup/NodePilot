import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * Drift guard for the `<select>` dropdown, following the pattern of `fontTokens.test.ts`:
 * the CSS source is read as text instead of imported.
 *
 * The popup is a separate surface that jsdom does not render and Playwright cannot reach, so
 * a mistake there surfaces only for a person who opens the dropdown. The native popup applies
 * only the base option styles, which is why `appearance: base-select` is required for
 * `:hover` and `:checked` to take effect.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const indexCss = readFileSync(join(__dirname, '..', '..', 'index.css'), 'utf8');

/** Comments stripped and whitespace collapsed: the file uses CRLF and selector lists
 *  span several lines. */
const cssFlat = indexCss.replaceAll(/\/\*[\s\S]*?\*\//g, ' ').replaceAll(/\s+/g, ' ');

/** Returns a nested block body by counting braces from `head`. */
function blockBody(css: string, head: string): string | null {
  const start = css.indexOf(head);
  if (start < 0) return null;
  let depth = 0;
  for (let i = css.indexOf('{', start); i < css.length; i++) {
    if (css[i] === '{') depth++;
    else if (css[i] === '}' && --depth === 0) return css.slice(css.indexOf('{', start) + 1, i);
  }
  return null;
}

/** Returns the rule body for an exact selector boundary. */
function ruleBody(css: string, selector: string): string | null {
  const escaped = selector.replaceAll(/\s+/g, ' ').trim().replaceAll(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);
  const match = new RegExp(`(?:^|[{};])\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css);
  return match ? match[1] : null;
}

const baseSelect = blockBody(cssFlat, '@supports (appearance: base-select)');

describe('native select popup theming', () => {
  it('declares the color-scheme hint for both bases', () => {
    // Native controls need this hint to use the dark system palette in dark themes.
    expect(indexCss).toMatch(/:root\s*\{\s*color-scheme:\s*light;\s*\}/);
    expect(indexCss).toMatch(/html\.dark\s*\{\s*color-scheme:\s*dark;\s*\}/);
  });

  it('keeps a themed surface for the native fallback popup', () => {
    // The fallback needs an opaque surface so light system menus do not hide light text.
    for (const selector of ['select', 'select option']) {
      const body = ruleBody(cssFlat, selector);
      expect(body, `${selector} must carry a surface in index.css`).not.toBeNull();
      expect(body).toContain('background-color: var(--color-surface-lowest)');
      expect(body).toContain('color: var(--color-on-surface)');
    }
  });
});

describe('customisable select (appearance: base-select)', () => {
  it('is opted into behind a @supports guard', () => {
    // Unsupported browsers ignore the guarded block and retain the native popup.
    expect(baseSelect, '@supports (appearance: base-select) block must exist').not.toBeNull();
    const optIn = ruleBody(baseSelect!, 'select, ::picker(select)');
    expect(optIn, 'both the button and its picker must opt in').not.toBeNull();
    expect(optIn).toContain('appearance: base-select');
  });

  it('styles the picker surface itself', () => {
    const picker = ruleBody(baseSelect!, '::picker(select)');
    expect(picker).not.toBeNull();
    expect(picker).toContain('background: var(--color-surface-lowest)');
    expect(picker).toContain('border: 1px solid var(--color-outline-variant)');
    // Long option lists must remain within the viewport.
    expect(picker).toMatch(/max-height:\s*\S+/);
    expect(picker).toContain('overflow-y: auto');
  });

  it('paints hover and the checked row itself — the whole point of the switch', () => {
    const hover = ruleBody(baseSelect!, 'select option:hover, select option:focus');
    expect(hover, 'option:hover is exactly what the native popup could not do').not.toBeNull();
    expect(hover).toContain('background: var(--color-surface-high)');

    const checked = ruleBody(baseSelect!, 'select option:checked');
    expect(checked).not.toBeNull();
    expect(checked).toContain('background: var(--color-primary-fixed)');
    expect(checked).toContain('color: var(--color-on-primary-fixed)');
  });

  it('keeps every popup colour token-driven so skins carry through', () => {
    for (const selector of [
      '::picker(select)',
      'select option',
      'select option:hover, select option:focus',
      'select option:checked',
      'select option:disabled',
      'select option::checkmark',
    ]) {
      const body = ruleBody(baseSelect!, selector);
      expect(body, `${selector} must exist inside the @supports block`).not.toBeNull();
      // Color literals would prevent themes from updating the popup; neutral shadows are exempt.
      expect(body!.replaceAll(/box-shadow:[^;]*;/g, '')).not.toMatch(/#[0-9a-f]{3,8}\b/i);
    }
  });
});
