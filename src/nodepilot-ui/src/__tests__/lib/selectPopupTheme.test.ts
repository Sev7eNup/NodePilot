import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * Drift-Guard für das `<select>`-Dropdown — nach dem Muster von `fontTokens.test.ts`:
 * die CSS-Quelle wird als Text gelesen, nicht importiert.
 *
 * Warum es diesen Test gibt: das Popup ist eine eigene Fläche. jsdom rendert es nicht,
 * Playwright kann es nicht anfassen — ein Fehler dort fällt in KEINER Suite auf, sondern
 * nur einem Menschen, der das Dropdown aufklappt. Und die Lösung ist alles andere als
 * selbsterklärend: das native Popup übernimmt nur die *Basis*-Styles der Options, deshalb
 * braucht es `appearance: base-select`, damit `:hover`/`:checked` überhaupt greifen. Genau
 * die Sorte Code, die beim „Aufräumen" vereinfacht und damit lautlos kaputtgemacht wird.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const indexCss = readFileSync(join(__dirname, '..', '..', 'index.css'), 'utf8');

/** Kommentarfrei + Whitespace normalisiert — die Working Copy ist CRLF, und die
 *  Selektorlisten stehen mehrzeilig. */
const cssFlat = indexCss.replaceAll(/\/\*[\s\S]*?\*\//g, ' ').replaceAll(/\s+/g, ' ');

/** Inhalt eines Blocks ab `head`, per Klammerzählung (verschachtelungsfest). */
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

/** Body einer Regel mit exakt diesem Selektorkopf. Die Grenze davor (`}`/`;`/`{`/Anfang)
 *  verhindert, dass `select` auch in `select option` trifft. */
function ruleBody(css: string, selector: string): string | null {
  const escaped = selector.replaceAll(/\s+/g, ' ').trim().replaceAll(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);
  const match = new RegExp(`(?:^|[{};])\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css);
  return match ? match[1] : null;
}

const baseSelect = blockBody(cssFlat, '@supports (appearance: base-select)');

describe('native select popup theming', () => {
  it('declares the color-scheme hint for both bases', () => {
    // Ohne diesen Hinweis zieht der Browser für alles Native (Scrollbars, Popup-Rahmen)
    // die helle System-Palette — auch unter einem dunklen Skin.
    expect(indexCss).toMatch(/:root\s*\{\s*color-scheme:\s*light;\s*\}/);
    expect(indexCss).toMatch(/html\.dark\s*\{\s*color-scheme:\s*dark;\s*\}/);
  });

  it('keeps a themed surface for the native fallback popup', () => {
    // Gilt weiter für alles ohne base-select: ein transparentes Select lässt das native
    // Popup aufs helle Systemmenü zurückfallen, während der Text hell bleibt.
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
    // Der Guard ist der Grund, warum es keinen Fallback-Zweig zu pflegen gibt: kennt der
    // Browser den Wert nicht, fällt der ganze Block weg und das native Popup bleibt.
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
    // Lange Listen dürfen das Popup nicht über den Viewport wachsen lassen.
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
      // Keine Farb-Literale — sonst friert das Popup auf einem Skin ein. Der reine
      // Schatten (rgb(0 0 0 / …)) ist skin-neutral und deshalb ausgenommen.
      expect(body!.replaceAll(/box-shadow:[^;]*;/g, '')).not.toMatch(/#[0-9a-f]{3,8}\b/i);
    }
  });
});
