import { useEffect, useRef, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeSlug from 'rehype-slug'
import rehypeHighlight from 'rehype-highlight'
import type { Components } from 'react-markdown'
import { getContent } from '../lib/content'
import { neighbors, pageByPath } from '../data/nav'
import { ArrowLeftIcon, ArrowRightIcon } from '../lib/icons'
import Toc from './Toc'

export default function DocPage({ path }: { path: string }) {
  const markdown = getContent(path)
  const scrollRef = useRef<HTMLDivElement>(null)
  const articleRef = useRef<HTMLElement>(null)

  // Reset scroll on navigation.
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: 0 })
  }, [path])

  if (!markdown) {
    return (
      <div className="mx-auto max-w-3xl px-6 py-16 text-center">
        <h1 className="text-2xl font-bold">Seite nicht gefunden</h1>
        <p className="mt-2 text-[var(--color-on-surface-variant)]">
          Inhalt für <code className="rounded bg-[var(--color-surface-container)] px-1">{path}</code> ist nicht verfügbar.
        </p>
        <Link to="/" className="mt-6 inline-block font-medium text-[var(--np-accent-text)] hover:underline">
          ← Zur Startseite
        </Link>
      </div>
    )
  }

  const meta = pageByPath(path)
  const { prev, next } = neighbors(path)

  return (
    <div className="flex w-full">
      {/* Main content column */}
      <div ref={scrollRef} className="np-scroll mx-auto flex min-w-0 max-w-3xl flex-1 px-6 py-8 lg:px-10">
        <article ref={articleRef} className="np-prose min-w-0 flex-1">
          {meta && (
            <p className="mb-2 text-xs font-medium uppercase tracking-wider text-[var(--color-on-surface-variant)]">
              {breadcrumb(path)}
            </p>
          )}
          <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[rehypeSlug, rehypeHighlight]}
            components={makeLinkComponents(path)}
          >
            {markdown}
          </ReactMarkdown>

          <hr className="my-10" />

          <nav className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {prev ? (
              <FooterLink kind="prev" path={prev.path} title={prev.title} />
            ) : (
              <span />
            )}
            {next ? <FooterLink kind="next" path={next.path} title={next.title} /> : <span />}
          </nav>
        </article>
      </div>

      {/* Right-side on-this-page TOC (desktop only) */}
      <Toc articleRef={articleRef} path={path} />
    </div>
  )
}

function breadcrumb(path: string): string {
  const seg = path.split('/')[0]
  const group = ({
    'getting-started': 'Erste Schritte',
    concepts: 'Konzepte',
    designer: 'Workflow-Designer',
    api: 'Referenz',
    security: 'Security',
    enterprise: 'Enterprise',
    configuration: 'Konfiguration',
    deployment: 'Deployment & Mehr',
  } as Record<string, string>)[seg]
  return group ?? 'Referenz'
}

function FooterLink({
  kind,
  path,
  title,
}: {
  kind: 'prev' | 'next'
  path: string
  title: string
}) {
  return (
    <Link
      to={`/${path}`}
      className={`group flex flex-col gap-1 rounded-lg border border-[var(--color-outline-variant)] px-4 py-3 transition-colors hover:border-[var(--np-accent-ring)] hover:bg-[var(--np-accent-soft)] ${
        kind === 'next' ? 'sm:text-right' : ''
      }`}
    >
      <span className="flex items-center gap-1 text-xs text-[var(--color-on-surface-variant)]">
        {kind === 'prev' ? (
          <>
            <ArrowLeftIcon className="h-3.5 w-3.5" /> Vorherige
          </>
        ) : (
          <>
            Weiter <ArrowRightIcon className="h-3.5 w-3.5" />
          </>
        )}
      </span>
      <span className="font-medium text-[var(--color-on-surface)] group-hover:text-[var(--np-accent-text)]">
        {title}
      </span>
    </Link>
  )
}

// Markdown component overrides. Headings keep their rehype-slug `id` natively
// (we deliberately do NOT render hash anchors here — clicking `#id` would collide
// with the HashRouter route living in location.hash and navigate to a 404).
// Section navigation is handled by the right-side <Toc/> via scrollIntoView.
//
// Internal cross-links (`./x`, `../group/page`, `/group/page`) are rewritten to
// react-router `<Link>` so they use HashRouter client-side navigation instead of
// a full page load — a plain `<a href="./installation">` would resolve against the
// document base URL (before the `#`), producing a broken `/installation` path and
// leaving the user on the current page. See makeLinkComponents() below.

/** Resolve a markdown cross-link href against the current doc path into a nav
 * path like "getting-started/installation". Returns null for non-internal links. */
function resolveDocHref(href: string, currentPath: string): string | null {
  if (!href) return null
  // External (http/https), mailto, tel, data — leave to the browser.
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//')) return null
  // In-page anchor (#slug) — handled separately by InternalLink via scroll.
  if (href.startsWith('#')) return null
  // Treat the current page path as a directory base ("getting-started/" for
  // "getting-started/introduction"; "" for top-level pages like "triggers").
  const baseDir = currentPath.includes('/') ? currentPath.replace(/[^/]*$/, '') : ''
  let pathname: string
  try {
    pathname = new URL(href, `http://docs.local/${baseDir}`).pathname
  } catch {
    return null
  }
  return pathname.replace(/^\//, '').replace(/\/+$/, '')
}

function InternalLink({ href, currentPath, children }: { href: string; currentPath: string; children?: ReactNode }) {
  // In-page anchor: scroll to the element instead of changing the hash route.
  if (href.startsWith('#')) {
    const id = href.slice(1)
    return (
      <a
        href={href}
        onClick={(e) => {
          e.preventDefault()
          document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
        }}
      >
        {children}
      </a>
    )
  }
  const target = resolveDocHref(href, currentPath)
  if (target === null) {
    return <a href={href}>{children}</a>
  }
  return (
    <Link to={`/${target}`}>
      {children}
    </Link>
  )
}

function makeLinkComponents(currentPath: string): Components {
  return {
    a: ({ href, children }) => {
      const external = /^https?:\/\//.test(href ?? '') || /^(mailto|tel):/i.test(href ?? '')
      if (external) {
        return (
          <a href={href} target="_blank" rel="noreferrer">
            {children}
          </a>
        )
      }
      return (
        <InternalLink href={href ?? ''} currentPath={currentPath}>
          {children}
        </InternalLink>
      )
    },
  }
}