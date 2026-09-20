/**
 * First-visit preferences for the demo: its language, and the Minimal Dark skin.
 *
 * A side-effect module, imported first in `main.demo.ts`, because both values are read while
 * their owners initialise — i18n during `src/i18n/index.ts`, the theme store while zustand
 * rehydrates. Static imports are evaluated before any function body runs, so this has to be an
 * import rather than a call inside `bootstrap()`.
 *
 * Each key is only seeded when nothing is stored. A visitor who switches language or skin keeps
 * their choice, and a reload — which resets the demo world — does not undo it.
 */
const LANGUAGE_KEY = 'nodepilot.lang';
const THEME_KEY = 'nodepilot.theme';

/** Matches the zustand persist envelope the theme store writes (`name` + default version 0). */
const MINIMAL_DARK = JSON.stringify({ state: { theme: 'dark-minimal' }, version: 0 });

function seed(key: string, value: string): void {
  try {
    if (!globalThis.localStorage?.getItem(key)) {
      globalThis.localStorage?.setItem(key, value);
    }
  } catch {
    // Storage can be disabled or blocked; the app then falls back to its own defaults.
  }
}

/**
 * The same rule the project website and the documentation follow: the first supported browser
 * language wins, otherwise English. Only the primary subtag counts, so `de-AT` resolves to `de`.
 *
 * Seeded rather than left to the product's own detector, because that one falls back to German —
 * a visitor whose browser asks for neither language would meet a German demo beside an English
 * website.
 */
function preferredLanguage(): string {
  for (const candidate of globalThis.navigator?.languages ?? []) {
    const primary = candidate.split('-')[0]?.toLowerCase();
    if (primary === 'de' || primary === 'en') return primary;
  }
  return 'en';
}

seed(LANGUAGE_KEY, preferredLanguage());
const requestedLanguage = new URLSearchParams(globalThis.location?.search ?? '').get('lang');
if (requestedLanguage === 'de' || requestedLanguage === 'en') {
  try { globalThis.localStorage?.setItem(LANGUAGE_KEY, requestedLanguage); } catch { /* storage is optional */ }
}

// The product opens on `system`, which follows the OS and makes the demo look different to
// every visitor. The demo pins one skin so the shop window is the same for everyone.
seed(THEME_KEY, MINIMAL_DARK);
