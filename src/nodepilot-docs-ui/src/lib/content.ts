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

export interface TocHeading {
  id: string
  text: string
  level: number
}

/**
 * Extract `##` / `###` headings for the right-side TOC.
 * Parses raw markdown rather than the rendered DOM so it is stable on first paint.
 */
export function extractToc(markdown: string): TocHeading[] {
  const lines = markdown.split('\n')
  const out: TocHeading[] = []
  let inFence = false
  for (const line of lines) {
    if (/^\s*```/.test(line)) {
      inFence = !inFence
      continue
    }
    if (inFence) continue
    const m = /^(#{2,3})\s+(.+?)\s*$/.exec(line)
    if (!m) continue
    const level = m[1].length
    const text = m[2].replace(/[#`*_]/g, '').trim()
    out.push({ id: slugify(text), text, level })
  }
  return out
}

export function slugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s-]/gu, '')
    .trim()
    .replace(/\s+/g, '-')
}