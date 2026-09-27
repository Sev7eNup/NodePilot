import catalog from './blog-catalog.json' with { type: 'json' }
import type { Lang } from '../i18n/languages'

export const articles = catalog
export const articleBySlug = Object.fromEntries(articles.map(article => [article.slug, article]))
export const articleTexts = Object.fromEntries((['de', 'en'] as const).map(lang => [lang,
  Object.fromEntries(articles.map(article => [article.slug, article.text[lang]])),
])) as Record<Lang, Record<string, (typeof catalog)[number]['text']['de']>>

export const blogAuthor = { '@type': 'Organization', name: 'NodePilot', url: 'https://github.com/Sev7eNup/NodePilot' }

export function visibleArticles(preview = false) {
  return articles.filter(article => preview || article.status === 'published')
    .sort((a, b) => Number(b.next) - Number(a.next))
}

export function isBlogPreview(doc: Document): boolean {
  return doc.querySelector('meta[name="np-blog-preview"]')?.getAttribute('content') === '1'
}
