import { readFileSync, existsSync } from 'node:fs'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeSlug from 'rehype-slug'

/** Article bodies are build inputs only. The browser receives the selected article's HTML. */
export function articleBody(slug, lang) {
  const file = new URL(`../content/blog/${slug}.${lang}.html`, import.meta.url)
  if (existsSync(file)) return readFileSync(file, 'utf8')
  const source = readFileSync(new URL(`../content/blog/${slug}.${lang}.md`, import.meta.url), 'utf8')
    .replace(/^# [^\r\n]+\r?\n/, '')
    .replace(/^\*\*(\d+\. .+)\*\*$/gm, '## $1')
  return renderToStaticMarkup(createElement(Markdown, {
    remarkPlugins: [remarkGfm], rehypePlugins: [rehypeSlug], children: source,
    // Prevent React from preloading the Markdown source URL before responsive URLs are resolved.
    components: { img: props => createElement('img', { src: props.src, alt: props.alt, title: props.title, loading: 'lazy', decoding: 'async' }) },
  }))
}
