import type { Lang } from '../i18n/languages'

/**
 * Address of a documentation page inside the router, always as a directory.
 *
 * The trailing slash is what Apache, GitHub Pages and the API all redirect to, because each
 * page is a real `index.html` in its own directory. Every link goes through here so that the
 * client-side address is byte-identical to the one the server hands out.
 */
export function docPath(lang: Lang, path = ''): string {
  return path === '' ? `/${lang}/` : `/${lang}/${path}/`
}
