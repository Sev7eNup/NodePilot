import { LANG_STORAGE_KEY, type Lang } from '../../i18n/languages'
import { de, type Messages } from './de'
import { en } from './en'

export type { Messages } from './de'

export const messages: Record<Lang, Messages> = { de, en }

// The static markup in index.html is German.
let current: Lang = 'de'

const listeners: Array<(lang: Lang) => void> = []

export function currentLang(): Lang {
  return current
}

/** Texts of the active language. */
export function t(): Messages {
  return messages[current]
}

/** Resolves a dotted key such as `home.title`. Undefined when the key is missing or not a text. */
export function lookup(dictionary: Messages, key: string): string | undefined {
  let node: unknown = dictionary
  for (const part of key.split('.')) {
    if (node === null || typeof node !== 'object') return undefined
    node = (node as Record<string, unknown>)[part]
  }
  return typeof node === 'string' ? node : undefined
}

/** Replaces `{name}` placeholders. */
export function format(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (match, name: string) => (name in values ? String(values[name]) : match))
}

/**
 * Prefix from the current document to the site root. Every prerendered route sits in its own
 * directory, so a link written for the root needs it; main.ts sets it once at boot.
 */
let basePrefix = ''

export function setBasePrefix(prefix: string): void {
  basePrefix = prefix
}

/** A link from the current document to something at the site root, such as the demo. */
export function sitePath(path = ''): string {
  return `${basePrefix}${path}`
}

/** Link into the docs, which live next to the website under docs/ and have real addresses. */
export function docsHref(lang: Lang, page = ''): string {
  return `${basePrefix}docs/${lang}/${page ? `${page}/` : ''}`
}

/** Registers a callback that re-renders texts set from script, such as the current route. */
export function onLanguageChange(listener: (lang: Lang) => void): void {
  listeners.push(listener)
}

/**
 * Switches every translated text in the document to `lang`: the data-i18n* attributes, the
 * language-aware docs links, the page language and description. Registered listeners then
 * re-render what script sets.
 */
export function applyLanguage(lang: Lang): void {
  current = lang
  const dictionary = messages[lang]
  document.documentElement.lang = lang

  // Elements include SVG text, so this stays on the Element interface.
  const fill = (attribute: string, apply: (element: Element, text: string) => void) => {
    for (const element of document.querySelectorAll(`[${attribute}]`)) {
      const text = lookup(dictionary, element.getAttribute(attribute) ?? '')
      if (text !== undefined) apply(element, text)
    }
  }
  fill('data-i18n', (element, text) => {
    element.textContent = text
  })
  fill('data-i18n-html', (element, text) => {
    // Only author-written dictionary HTML is inserted here.
    element.innerHTML = text
  })
  fill('data-i18n-aria-label', (element, text) => element.setAttribute('aria-label', text))
  fill('data-i18n-title', (element, text) => element.setAttribute('title', text))
  fill('data-i18n-placeholder', (element, text) => element.setAttribute('placeholder', text))

  for (const link of document.querySelectorAll('[data-docs-path]')) {
    link.setAttribute('href', docsHref(lang, link.getAttribute('data-docs-path') ?? ''))
  }
  document.querySelector('meta[name="description"]')?.setAttribute('content', dictionary.meta.description)

  for (const listener of listeners) listener(lang)
}

/** Remembers an explicit language choice for the website and the docs. */
export function persistLang(lang: Lang): void {
  try {
    window.localStorage.setItem(LANG_STORAGE_KEY, lang)
  } catch {
    // Blocked storage: the choice lasts for this page view only.
  }
}
