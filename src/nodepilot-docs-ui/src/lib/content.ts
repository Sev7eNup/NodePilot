import { allPages } from '../data/nav'

// Eager-import every Markdown file under content/ as a raw string.
// Vite resolves this at build time → keyed map of { "getting-started/introduction": "...md" }.
const modules = import.meta.glob('../../content/**/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

export const contentMap: Record<string, string> = {}
for (const [filePath, raw] of Object.entries(modules)) {
  const key = filePath.replace(/^.*\/content\//, '').replace(/\.md$/, '')
  contentMap[key] = raw
}

export function getContent(path: string): string | undefined {
  return contentMap[path]
}

/** Every page that resolves to actual content (intersection of nav + files). */
export const availablePages = allPages.filter((p) => Boolean(contentMap[p.path]))