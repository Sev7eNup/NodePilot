/** Converts old demo bookmarks before preferences and the app read the address. */
export function legacyDemoPath(href: string): string | null {
  const source = new URL(href);
  if (!source.hash.startsWith('#/')) return null;
  if (!URL.canParse(source.hash.slice(1), source.origin)) return '/demo/';
  const route = new URL(source.hash.slice(1), source.origin);
  if (route.origin !== source.origin || !route.pathname.startsWith('/')) return '/demo/';
  const query = new URLSearchParams(source.search);
  for (const key of new Set(route.searchParams.keys())) {
    query.delete(key);
    for (const value of route.searchParams.getAll(key)) query.append(key, value);
  }
  return `/demo${route.pathname}${query.size ? `?${query}` : ''}${route.hash}`;
}

const target = legacyDemoPath(location.href);
if (target) history.replaceState(history.state, '', target);
