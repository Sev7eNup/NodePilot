import { Link } from 'react-router'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeSlug from 'rehype-slug'
import rehypeHighlight from 'rehype-highlight'
import type { Lang } from '../i18n/languages'
import { docPath } from '../lib/docPath'

/** Markdown paths refer to source siblings, not the published index.html directory. */
export function resolveDocLink(href: string, currentPath: string, lang: Lang): string | null {
  if (!href || href.startsWith('#') || href.startsWith('//') || /^[a-z][a-z0-9+.-]*:/i.test(href)) return null
  const base = currentPath.includes('/') ? currentPath.replace(/[^/]*$/, '') : ''
  const url = new URL(href, `https://docs.invalid/${base}`)
  const path = url.pathname.replace(/^\//, '').replace(/\/+$/, '').replace(/\.md$/, '')
  return `${docPath(lang, path)}${url.search}${url.hash}`
}

/** Shared by the static build and the interactive docs, including heading IDs and links. */
export default function DocMarkdown({ markdown, lang, path }: { markdown: string; lang: Lang; path: string }) {
  return <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSlug, rehypeHighlight]} components={{
    a: ({ href = '', children }) => {
      const target = resolveDocLink(href, path, lang)
      if (target !== null) return <Link to={target}>{children}</Link>
      if (/^https?:\/\//.test(href)) return <a href={href} target="_blank" rel="noreferrer">{children}</a>
      return <a href={href}>{children}</a>
    },
  }}>{markdown}</ReactMarkdown>
}
