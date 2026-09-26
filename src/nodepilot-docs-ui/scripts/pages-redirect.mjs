/** Maps every old Pages address to a fixed origin, including pre-path-routing bookmarks. */
export function pagesRedirectTarget(href) {
  const source = new URL(href)
  const target = new URL('https://www.nodepilot.run/')
  const prefix = '/NodePilot'
  let path = source.pathname === prefix ? '/' : source.pathname.startsWith(`${prefix}/`)
    ? source.pathname.slice(prefix.length) : '/'
  let hash = source.hash
  const query = new URLSearchParams(source.search)
  const renamed = { produkt: 'product', erleben: 'walkthrough', 'warum-nodepilot': 'why-nodepilot' }
  const siteRoutes = ['product', 'walkthrough', 'blog', 'impressum', 'datenschutz']
  const languages = ['en', 'de']

  if (hash.startsWith('#/')) {
    if (!URL.canParse(hash.slice(1), target.origin)) return target.href
    const route = new URL(hash.slice(1), target.origin)
    // Reject protocol-relative or backslash-based external addresses.
    if (route.origin !== target.origin) return target.href
    let legacyPath = route.pathname.replace(/^\/+|\/+$/g, '')
    if (path === '/demo' || path.startsWith('/demo/')) {
      path = `/demo/${legacyPath}`
    } else if (path === '/docs' || path.startsWith('/docs/')) {
      if (legacyPath && !languages.includes(legacyPath.split('/')[0])) legacyPath = `en/${legacyPath}`
      path = `/docs/${legacyPath}${legacyPath ? '/' : ''}`
    } else if (path === '/' || path === '/index.html') {
      const parts = legacyPath.split('/').map(segment => renamed[segment] ?? segment)
      if (!legacyPath) path = '/'
      else if (siteRoutes.includes(parts[0])) path = `/${parts.join('/')}/`
      else path = `/docs/${languages.includes(parts[0]) ? '' : 'en/'}${legacyPath}/`
    }
    for (const key of new Set(route.searchParams.keys())) {
      query.delete(key)
      for (const value of route.searchParams.getAll(key)) query.append(key, value)
    }
    hash = route.hash
  }

  if (path === '/produkt/' || path === '/produkt') path = '/product/'
  if (path === '/erleben/' || path === '/erleben') path = '/walkthrough/'
  if (path === '/blog/warum-nodepilot/' || path === '/blog/warum-nodepilot') path = '/blog/why-nodepilot/'
  // Assigning pathname cannot change the destination origin, even for a hostile source path.
  target.pathname = path
  target.search = query.toString()
  target.hash = hash
  return target.href
}
