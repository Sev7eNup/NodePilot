/**
 * Demo chrome copy, in the two languages the project site is published in.
 *
 * Deliberately not in `src/i18n/locales/**`: those bundles ship with the product, and demo
 * strings have no business there.
 */
export type DemoLanguage = 'de' | 'en';

interface DemoStrings {
  badge: string;
  message: string;
  reset: string;
  resetTitle: string;
  dismiss: string;
  restore: string;
  tour: string;
  website: string;
}

const STRINGS: Record<DemoLanguage, DemoStrings> = {
  en: {
    badge: 'Demo',
    message: 'Simulated data. Everything runs in your browser — nothing is sent to a server, and no one else sees your changes.',
    reset: 'Reset',
    resetTitle: 'Discard your changes and restore the sample data',
    dismiss: 'Hide',
    restore: 'Demo notice',
    tour: 'Guided tour',
    website: 'NodePilot website',
  },
  de: {
    badge: 'Demo',
    message: 'Simulierte Daten. Alles läuft in Ihrem Browser — nichts wird an einen Server gesendet, und niemand sonst sieht Ihre Änderungen.',
    reset: 'Zurücksetzen',
    resetTitle: 'Änderungen verwerfen und die Beispieldaten wiederherstellen',
    dismiss: 'Ausblenden',
    restore: 'Demo-Hinweis',
    tour: 'Geführter Einstieg',
    website: 'NodePilot-Website',
  },
};

/**
 * Follows the app's own language choice.
 *
 * Reads the same `localStorage` key i18n uses, which `boot/language.ts` seeds to English on a
 * first visit. Reading `navigator.language` instead left the banner in German while the app
 * around it was English.
 */
export function demoLanguage(): DemoLanguage {
  const requested = new URLSearchParams(globalThis.location?.search ?? '').get('lang');
  if (requested === 'de' || requested === 'en') return requested;
  try {
    const stored = globalThis.localStorage?.getItem('nodepilot.lang');
    if (stored?.startsWith('de')) return 'de';
  } catch {
    // Storage can be disabled; English is the demo's default either way.
  }
  return 'en';
}

export function demoStrings(language: DemoLanguage = demoLanguage()): DemoStrings {
  return STRINGS[language];
}
