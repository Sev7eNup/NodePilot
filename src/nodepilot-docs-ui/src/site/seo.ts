import { configureImage, type ImageKey } from './media'
import { articleBySlug, blogAuthor, isBlogPreview, visibleArticles } from './blog'
import type { Lang } from '../i18n/languages'
import { format, messages, lookup } from './i18n'
import { localizedPath, resolveRoute, SOLUTION_SLUGS, type SiteRoute } from './router'
import { solutions } from './solutions'
import { formatDuration, videoEpisodes } from './videos'

export function siteHref(base: string, route: SiteRoute, lang: Lang): string {
  const path = localizedPath(route, lang)
  return `${base}${path}${path ? '/' : ''}`
}

export function siteMeta(route: SiteRoute, lang: Lang) {
  const copy = messages[lang]
  if (route.page === 'article') {
    // The search title carries the words people search for; the page keeps its editorial h1.
    const article = copy.articles[route.slug]
    return { title: `${article.seoTitle} | NodePilot Blog`, description: article.summary }
  }
  if (route.page === 'solution') {
    const solution = solutions[lang][route.slug]
    return { title: `${solution.title} | NodePilot`, description: solution.summary }
  }
  return { title: `${copy.titles[route.page]} | NodePilot`, description: copy.meta.descriptions[route.page] }
}

/** Same content and links during the build and in the browser. No crawler-only copy. */
export function renderSiteContent(doc: Document, route: SiteRoute, lang: Lang, base: string, articleBody?: string): void {
  const copy = messages[lang]
  const available = visibleArticles(isBlogPreview(doc))
  const entry = route.page === 'article' ? articleBySlug[route.slug] : undefined
  doc.documentElement.lang = lang
  for (const [attr, target] of Object.entries({ 'data-i18n': 'textContent', 'data-i18n-html': 'innerHTML', 'data-i18n-aria-label': 'aria-label', 'data-i18n-title': 'title', 'data-i18n-placeholder': 'placeholder' })) {
    for (const element of doc.querySelectorAll(`[${attr}]`)) {
      const text = lookup(copy, element.getAttribute(attr) ?? '')
      if (text === undefined) continue
      if (target === 'textContent') element.textContent = text
      else if (target === 'innerHTML') element.innerHTML = text
      else element.setAttribute(target, text)
    }
  }
  const fill = (id: string, text: string, html = false) => {
    const element = doc.getElementById(id)
    if (element) { if (html) element.innerHTML = text; else element.textContent = text }
  }
  fill('header-current', copy.pages[route.page])
  const productImage = doc.getElementById('product-image')
  if (productImage && !productImage.hasAttribute('data-request')) productImage.setAttribute('alt', copy.screens.designer.alt)
  const navKey = route.page === 'article' ? 'blog' : route.page === 'solution' ? `solution:${route.slug}` : route.page
  for (const link of doc.querySelectorAll('[data-nav]')) {
    const active = link.getAttribute('data-nav') === navKey
    link.classList.toggle('is-active', active)
    if (active) link.setAttribute('aria-current', 'page')
    else link.removeAttribute('aria-current')
  }
  if (route.page === 'article') {
    const article = copy.articles[route.slug]
    fill('article-category', article.category)
    fill('article-title', article.title)
    fill('article-lead', article.lead)
    if (articleBody !== undefined) fill('article-body', articleBody, true)
    const meta = doc.querySelector('#article-page .article-meta')
    if (meta && entry) {
      meta.innerHTML = `<a href="${blogAuthor.url}" rel="author">${blogAuthor.name}</a>` +
        (entry.publishedAt ? ` · <time datetime="${entry.publishedAt}">${lang === 'de' ? 'Veröffentlicht am' : 'Published'} ${entry.publishedAt}</time>` : ` · ${lang === 'de' ? 'Lokale Vorschau · noch nicht veröffentlicht' : 'Local preview · unpublished'}`) +
        (entry.modifiedAt ? ` · <time datetime="${entry.modifiedAt}">${lang === 'de' ? 'Aktualisiert am' : 'Updated'} ${entry.modifiedAt}</time>` : '')
    }
  }
  if (route.page === 'solution') {
    const solution = solutions[lang][route.slug]
    fill('solution-title', solution.title)
    fill('solution-lead', solution.summary)
    fill('solution-body', solution.body, true)
  }
  for (const body of doc.querySelectorAll('#article-body, #solution-body')) {
    const used = new Set<string>()
    for (const heading of body.querySelectorAll('h2, h3')) {
      const stem = heading.id || (heading.textContent ?? '').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '')
      let id = stem
      for (let i = 2; used.has(id); i++) id = `${stem}-${i}`
      heading.id = id
      used.add(id)
    }
  }
  let breadcrumb = doc.getElementById('site-breadcrumbs')
  if (!breadcrumb) {
    breadcrumb = doc.createElement('nav')
    breadcrumb.id = 'site-breadcrumbs'
    breadcrumb.className = 'site-breadcrumbs'
    doc.getElementById('main-content')?.prepend(breadcrumb)
  }
  breadcrumb.setAttribute('aria-label', lang === 'de' ? 'Seitenpfad' : 'Breadcrumb')
  breadcrumb.hidden = route.page === 'home' || route.page === 'notfound'
  breadcrumb.replaceChildren()
  const homeLink = doc.createElement('a')
  homeLink.textContent = 'NodePilot'
  homeLink.setAttribute('data-site-path', '')
  breadcrumb.appendChild(homeLink)
  if (route.page === 'article') {
    const blogLink = doc.createElement('a')
    blogLink.textContent = 'Blog'
    blogLink.setAttribute('data-site-path', 'blog')
    breadcrumb.append(' / ', blogLink)
  }
  breadcrumb.append(` / ${siteMeta(route, lang).title.replace(/ \| NodePilot(?: Blog)?$/, '')}`)
  // Contextual links are part of the visible page, rather than an SEO-only hidden list.
  for (const holder of doc.querySelectorAll('[data-topic-links]')) {
    holder.innerHTML = `<h2>${lang === 'de' ? 'Automatisierung in der eigenen Umgebung' : 'Automation in your environment'}</h2><ul>` +
      SOLUTION_SLUGS.map(slug => `<li><a data-site-path="${slug}">${solutions[lang][slug].title}</a></li>`).join('') + '</ul>'
  }
  for (const holder of doc.querySelectorAll('[data-related-articles]')) {
    const related = available.filter(article => entry ? entry.related.includes(article.slug) : route.page === 'solution' ? article.solution === route.slug : true).slice(0, 3)
    const docs = entry?.docs ?? 'getting-started/quickstart'
    holder.innerHTML = `<h2>${lang === 'de' ? 'Passende Beiträge und nächste Schritte' : 'Related articles and next steps'}</h2><ul>` +
      related.map(article => `<li><a data-site-path="blog/${article.slug}">${copy.articles[article.slug].title}</a></li>`).join('') +
      (entry ? `<li><a data-site-path="${entry.solution}">${solutions[lang][entry.solution as keyof typeof solutions.de].title}</a></li>` : '') +
      `<li><a data-docs-path="${docs}">${entry ? entry.docsTitle[lang] : lang === 'de' ? 'Schnelleinstieg in NodePilot' : 'NodePilot quickstart'}</a></li></ul>`
  }
  const index = doc.querySelector('.blog-index')
  if (index) {
    index.replaceChildren()
    for (const [i, article] of available.entries()) {
      const text = article.text[lang]
      const row = doc.createElement('a')
      row.className = 'blog-index-row'
      row.setAttribute('data-site-path', `blog/${article.slug}`)
      row.setAttribute('data-category', article.text.de.category === 'HINTERGRUND' ? 'hintergrund' : 'praxis')
      row.setAttribute('data-search', `${text.title} ${text.summary} ${article.question[lang]} ${article.slug}`)
      row.innerHTML = `<div class="blog-index-symbol" aria-hidden="true">${String(i + 1).padStart(2, '0')}</div><div><div class="article-category"></div><h2></h2><p></p><span class="read-more"></span></div>`
      row.querySelector('.article-category')!.textContent = text.category + (article.status === 'draft' ? (lang === 'de' ? ' · VORSCHAU' : ' · PREVIEW') : '')
      row.querySelector('h2')!.textContent = text.title
      row.querySelector('p')!.textContent = text.teaser
      row.querySelector('.read-more')!.textContent = copy.blog.readMore
      index.appendChild(row)
    }
  }
  const videoGrid = doc.getElementById('video-grid')
  if (videoGrid) {
    // Absolute from the site root: a relative media/ would resolve below /en/tutorials/.
    videoGrid.replaceChildren()
    for (const episode of videoEpisodes) {
      const text = episode.text[lang]
      const item = doc.createElement('li')
      item.innerHTML = '<a class="video-card"><span class="video-thumb"><img alt="" width="640" height="360" loading="lazy" decoding="async"><span class="video-play" aria-hidden="true"><svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5.5v13l11-6.5z"/></svg></span><span class="video-duration"></span></span><span class="video-episode"></span><h2 class="video-title"></h2></a>'
      const card = item.querySelector('a')!
      card.setAttribute('href', `${base}${text.video}`)
      card.setAttribute('data-video', episode.slug)
      card.setAttribute('aria-label', format(copy.videos.play, { title: text.title }))
      if (text.youtube) card.setAttribute('data-youtube', text.youtube)
      item.querySelector('img')!.setAttribute('src', `${base}${text.poster}`)
      item.querySelector('.video-duration')!.textContent = formatDuration(text.duration)
      item.querySelector('.video-episode')!.textContent = format(copy.videos.episode, { number: String(episode.number).padStart(2, '0') })
      item.querySelector('.video-title')!.textContent = text.title
      videoGrid.appendChild(item)
    }
  }
  for (const count of doc.querySelectorAll('.nav-count, .filter-count')) count.textContent = String(available.length).padStart(2, '0')
  for (const image of doc.querySelectorAll('[data-media]')) configureImage(image, image.getAttribute('data-media') as ImageKey, base)
  for (const image of doc.querySelectorAll('#article-body img')) {
    const name = image.getAttribute('src')?.split('/').pop()
    const key = ({ 'designer-beispiel.png': 'first-workflow-designer', 'ausfuehrung-beispiel.png': 'first-workflow-run', 'designer-dark.png': 'designer', 'ai-dark.png': 'ai' } as Record<string, ImageKey>)[name ?? '']
    if (key) configureImage(image, key, base)
  }
  const encodePath = (path: string) => path.split('/').map(segment => encodeURIComponent(segment)).join('/')
  for (const image of doc.querySelectorAll('[data-site-image]')) image.setAttribute('src', `${base}${encodePath(image.getAttribute('data-site-image') ?? '')}`)
  for (const link of doc.querySelectorAll('[data-docs-path]')) {
    const path = link.getAttribute('data-docs-path') || 'getting-started/introduction'
    link.setAttribute('href', `${base}docs/${lang}/${encodePath(path)}/`)
  }
  for (const link of doc.querySelectorAll('a')) {
    if (link.hasAttribute('data-lang') || link.hasAttribute('data-docs-path')) continue
    // Remember the language-independent target before rewriting it for another language.
    let target = link.getAttribute('data-site-path')
    if (target === null) {
      const href = link.getAttribute('href') ?? ''
      if (!href || /^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(href)) continue
      const path = href.startsWith(base) ? href.slice(base.length) : href.replace(/^\.\/?/, '')
      const candidate = resolveRoute(path)
      if (candidate.page === 'notfound') continue
      target = localizedPath(candidate, 'de')
      link.setAttribute('data-site-path', target)
    }
    const resolved = resolveRoute(target)
    if (resolved.page === 'article' && !available.some(article => article.slug === resolved.slug)) {
      link.setAttribute('href', `${base}docs/${lang}/${articleBySlug[resolved.slug].docs}/`)
    } else if (resolved.page !== 'notfound') link.setAttribute('href', siteHref(base, resolved, lang))
  }
  for (const link of doc.querySelectorAll('[data-lang]')) {
    const language = link.getAttribute('data-lang') === 'en' ? 'en' : 'de'
    link.setAttribute('href', siteHref(base, route.page === 'notfound' ? { page: 'home' } : route, language))
    link.setAttribute('hreflang', language)
    link.setAttribute('aria-pressed', String(language === lang))
  }
}

export function renderSiteHead(doc: Document, route: SiteRoute, lang: Lang, origin: string): void {
  const base = `${new URL(origin).pathname.replace(/\/+$/, '')}/`
  const absolute = (r: SiteRoute, l = lang) => new URL(siteHref(base, r, l), origin).href
  const url = absolute(route)
  const meta = siteMeta(route, lang)
  doc.title = meta.title
  const set = (selector: string, name: string, value: string) => doc.querySelector(selector)?.setAttribute(name, value)
  set('meta[name="description"]', 'content', meta.description)
  set('meta[property="og:title"]', 'content', meta.title)
  set('meta[property="og:description"]', 'content', meta.description)
  set('meta[property="og:type"]', 'content', route.page === 'article' ? 'article' : 'website')
  set('meta[property="og:image:alt"]', 'content', lang === 'de' ? 'NodePilot – agentenlose Windows-Automatisierung' : 'NodePilot – agentless Windows automation')
  const ensure = (selector: string, tag: string, attributes: Record<string, string>) => {
    let element = doc.querySelector(selector)
    if (!element) { element = doc.createElement(tag); doc.head.appendChild(element) }
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value)
    return element
  }
  ensure('meta[property="og:locale"]', 'meta', { property: 'og:locale', content: lang === 'de' ? 'de_DE' : 'en_US' })
  for (const tag of doc.querySelectorAll('link[hreflang], script[data-site-schema], meta[name="robots"]')) tag.remove()
  if (route.page === 'notfound') {
    doc.querySelector('link[rel="canonical"]')?.remove()
    doc.querySelector('meta[property="og:url"]')?.remove()
    ensure('meta[name="robots"]', 'meta', { name: 'robots', content: 'noindex, follow' })
    return
  }
  ensure('link[rel="canonical"]', 'link', { rel: 'canonical', href: url })
  ensure('meta[property="og:url"]', 'meta', { property: 'og:url', content: url })
  if (route.page !== 'impressum' && route.page !== 'datenschutz') {
    for (const language of ['de', 'en', 'x-default'] as const) {
      // x-default is English, as in the docs: visitors in any other language read English more often than German.
      ensure(`link[hreflang="${language}"]`, 'link', { rel: 'alternate', hreflang: language, href: absolute(route, language === 'de' ? 'de' : 'en') })
    }
  }
  const entry = route.page === 'article' ? articleBySlug[route.slug] : undefined
  if (entry?.status === 'draft') ensure('meta[name="robots"]', 'meta', { name: 'robots', content: 'noindex, follow' })
  const home = absolute({ page: 'home' })
  // The project as publisher, with its logo, so search engines can tell it from other products of the same name.
  const organization = { '@id': `${origin}/#organization` }
  const graph: Record<string, unknown>[] = [
    { '@type': 'Organization', ...organization, name: 'NodePilot', url: `${origin}/`, logo: new URL(`${base}logo.png`, origin).href, sameAs: [blogAuthor.url] },
    { '@type': 'WebSite', '@id': `${origin}/#website`, url: `${origin}/`, name: 'NodePilot', inLanguage: ['de', 'en'], publisher: organization },
  ]
  graph.push({ '@type': route.page === 'article' ? 'BlogPosting' : 'WebPage', '@id': `${url}#page`, url, headline: meta.title.replace(/ \| NodePilot(?: Blog)?$/, ''), description: meta.description, inLanguage: lang, isPartOf: { '@id': `${origin}/#website` }, ...(route.page === 'article' ? { author: { ...blogAuthor, ...organization }, publisher: organization, ...(entry?.publishedAt ? { datePublished: entry.publishedAt } : {}), ...(entry?.modifiedAt ? { dateModified: entry.modifiedAt } : {}), mainEntityOfPage: url, image: new URL(`${base}og-image.png`, origin).href } : {}) })
  if (route.page === 'home') graph.push({ '@type': 'SoftwareApplication', name: 'NodePilot', url, applicationCategory: 'DeveloperApplication', operatingSystem: 'Windows', license: 'https://www.apache.org/licenses/LICENSE-2.0', downloadUrl: 'https://github.com/Sev7eNup/NodePilot/releases', sameAs: 'https://github.com/Sev7eNup/NodePilot', publisher: organization, offers: { '@type': 'Offer', price: '0', priceCurrency: 'EUR' } })
  if (route.page !== 'home') {
    const crumbs = [{ name: 'NodePilot', item: home }]
    if (route.page === 'article') crumbs.push({ name: 'Blog', item: absolute({ page: 'blog' }) })
    crumbs.push({ name: meta.title.replace(/ \| NodePilot(?: Blog)?$/, ''), item: url })
    graph.push({ '@type': 'BreadcrumbList', itemListElement: crumbs.map((crumb, i) => ({ '@type': 'ListItem', position: i + 1, ...crumb })) })
  }
  const script = doc.createElement('script')
  script.type = 'application/ld+json'
  script.setAttribute('data-site-schema', '')
  script.textContent = JSON.stringify({ '@context': 'https://schema.org', '@graph': graph }).replace(/</g, '\\u003c')
  doc.head.appendChild(script)
}
