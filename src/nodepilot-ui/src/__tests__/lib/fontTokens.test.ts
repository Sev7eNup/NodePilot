import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * Drift-Guard für das Type-System der App: IBM Plex Sans (UI) + IBM Plex Mono (Code).
 *
 * Warum es diesen Test gibt: `--font-mono` war vor der Umstellung schlicht NIE
 * deklariert. Tailwind fällt in dem Fall still auf seinen eigenen Default zurück,
 * es gibt keine Fehlermeldung und im Browser sieht alles plausibel aus — nur eben
 * je nach Betriebssystem in einer anderen Schrift. Genau solche stillen Lücken
 * fängt dieser Test, gebaut nach dem Muster von `activityCssPalette.test.ts`:
 * die CSS-/TS-Quellen werden als Text gelesen, nicht importiert.
 *
 * Der Quelltext-Ansatz ist bei `monacoSetup.ts` sogar zwingend — das Modul ist in
 * `__tests__/setup.ts` global gemockt, ein Import würde den Mock-Wert prüfen und
 * damit exakt nichts aussagen.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const uiSrc = join(__dirname, '..', '..');

const indexCss = readFileSync(join(uiSrc, 'index.css'), 'utf8');
const atelierCss = readFileSync(join(uiSrc, 'styles', 'designer-atelier.css'), 'utf8');
const monacoSetup = readFileSync(join(uiSrc, 'lib', 'monacoSetup.ts'), 'utf8');
const packageJson = JSON.parse(readFileSync(join(uiSrc, '..', 'package.json'), 'utf8'));

/**
 * Liest den Wert einer Custom-Property-DEKLARATION (`--foo: bar;`).
 * Aufrufstellen (`var(--foo)`) und Erwähnungen in Kommentaren haben keinen
 * Doppelpunkt direkt hinter dem Namen und matchen deshalb nicht — sonst würde ein
 * Kommentar vortäuschen, das Token sei definiert.
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
      // Generischer Fallback am Ende — ohne ihn hat der Browser bei fehlender
      // Schrift keinen definierten Rückweg (css:S4649).
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
    // Monaco misst Zeichenbreiten selbst in JS und sanitisiert den fontFamily-String,
    // kann also keine CSS-Variable verwerten. Der Stack steht deshalb ein zweites Mal
    // als TS-Konstante da — diese Assertion ist der Preis dafür.
    const match = /export const MONO_FONT_STACK\s*=\s*"([^"]+)"/.exec(monacoSetup);
    expect(match, 'MONO_FONT_STACK must be exported from lib/monacoSetup.ts').not.toBeNull();
    expect(match![1]).toBe(declaredValue(indexCss, '--font-mono'));
  });

  it('lets the Atelier skin follow the app mono stack instead of duplicating it', () => {
    expect(declaredValue(atelierCss, '--wd-mono')).toBe('var(--font-mono)');
  });

  it('self-hosts both families and drops the previous ones', () => {
    // Die Prod-CSP (SecurityPipelineSetup.cs) kennt kein `font-src` und fällt auf
    // `default-src 'self'` — ein CDN-Import wäre in Produktion hart geblockt.
    expect(indexCss).toContain("@import '@fontsource-variable/ibm-plex-sans';");
    expect(indexCss).toContain("@import '@fontsource/ibm-plex-mono/400.css';");
    expect(indexCss).toContain("@import '@fontsource/ibm-plex-mono/600.css';");
    expect(indexCss).not.toMatch(/@import\s+'@fontsource[^']*\/(inter|geist)/);
    expect(indexCss).not.toContain('Inter Variable');

    const deps = packageJson.dependencies as Record<string, string>;
    expect(deps).toHaveProperty('@fontsource-variable/ibm-plex-sans');
    expect(deps).toHaveProperty('@fontsource/ibm-plex-mono');
    expect(deps).not.toHaveProperty('@fontsource-variable/inter');
    // Geist war schon vor der Umstellung eine Leiche: deklariert, nie importiert.
    // Nur die Docs-Site nutzt es, und die bringt ihre eigene Dependency mit.
    expect(deps).not.toHaveProperty('@fontsource-variable/geist');
  });

  it('compensates the smaller x-height on body but not on monospace', () => {
    // Plex Sans hat eine kleinere x-Höhe als das zuvor genutzte Inter. In einer UI,
    // die fast durchgehend auf text-xs/text-sm steht, ist das sichtbar — deshalb der
    // Ausgleich. Code bleibt davon ausgenommen, damit die Spaltenbündigkeit hält.
    expect(/font-size-adjust:\s*0?\.\d+/.test(indexCss)).toBe(true);
    expect(indexCss).toMatch(/\.font-mono\s*\{\s*\n?\s*font-size-adjust:\s*none;/);
  });
});
