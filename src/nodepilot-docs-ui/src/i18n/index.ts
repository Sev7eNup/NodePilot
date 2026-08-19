import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'

import de from './locales/de.json'
import en from './locales/en.json'
import { DEFAULT_LANG, detectLang } from './languages'

/**
 * One namespace, two languages — the docs site is small enough that the app shell's
 * 30-namespace split would be pure overhead here.
 *
 * No browser-language detector plugin: the URL carries the language (`#/de/...`), so the
 * router is the single source of truth and `App` pushes every change in here. Detection
 * only ever runs once, for the initial redirect, and lives in `languages.ts` because the
 * router needs it before an i18n instance exists.
 */
void i18n.use(initReactI18next).init({
  resources: {
    de: { translation: de },
    en: { translation: en },
  },
  lng: detectLang(),
  fallbackLng: DEFAULT_LANG,
  interpolation: {
    // React already escapes rendered values.
    escapeValue: false,
  },
})

export default i18n
