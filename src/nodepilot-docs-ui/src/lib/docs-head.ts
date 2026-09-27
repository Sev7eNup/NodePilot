import type { Lang } from '../i18n/languages'
import { getContent, hasTranslation } from './content'
import { summarize } from './markdown-summary'

/** Keep client navigation consistent with the files emitted by prerenderDocs. */
export function updateDocsHead(lang: Lang, path: string, title: string): void {
  const markdown = getContent(lang, path)
  const origin = __NP_SITE_ORIGIN__.replace(/\/+$/, '')
  const url = `${origin}/docs/${lang}/${path}/`
  const set = (selector: string, tag: string, attrs: Record<string, string>) => {
    let element = document.querySelector(selector)
    if (!element) { element = document.createElement(tag); document.head.appendChild(element) }
    for (const [key, value] of Object.entries(attrs)) element.setAttribute(key, value)
    return element
  }
  document.title = title
  document.querySelectorAll('link[hreflang], meta[name="robots"], script[data-docs-schema]').forEach(tag => tag.remove())
  if (!markdown || !hasTranslation(lang, path)) {
    document.querySelector('link[rel="canonical"]')?.remove()
    document.querySelector('meta[property="og:url"]')?.remove()
    set('meta[name="robots"]', 'meta', { name: 'robots', content: 'noindex, follow' })
    return
  }
  const description = summarize(markdown)
  set('meta[name="description"]', 'meta', { name: 'description', content: description })
  set('link[rel="canonical"]', 'link', { rel: 'canonical', href: url })
  for (const [property, content] of Object.entries({ 'og:title': title, 'og:description': description, 'og:url': url }))
    set(`meta[property="${property}"]`, 'meta', { property, content })
  for (const language of ['de', 'en', 'x-default'] as const) {
    const translated = language === 'x-default' ? 'en' : language
    if (hasTranslation(translated, path)) set(`link[hreflang="${language}"]`, 'link', { rel: 'alternate', hreflang: language, href: `${origin}/docs/${translated}/${path}/` })
  }
  const schema = set('script[data-docs-schema]', 'script', { type: 'application/ld+json', 'data-docs-schema': '' })
  schema.textContent = JSON.stringify({ '@context': 'https://schema.org', '@type': 'TechArticle', headline: title.replace(/ — NodePilot.*$/, ''), description, inLanguage: lang, url, mainEntityOfPage: url }).replace(/</g, '\\u003c')
}
