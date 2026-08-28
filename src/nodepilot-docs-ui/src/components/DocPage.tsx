import { useEffect, useRef, type ReactNode } from 'react'
import { Link } from 'react-router'
import { Trans, useTranslation } from 'react-i18next'
import { ArrowLeft, ArrowRight } from '@carbon/icons-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeSlug from 'rehype-slug'
import rehypeHighlight from 'rehype-highlight'
import type { Components } from 'react-markdown'
import { getContent, hasTranslation } from '../lib/content'
import { navTitleKey, neighbors } from '../data/nav'
import { DEFAULT_LANG, type Lang } from '../i18n/languages'
import Toc from './Toc'

export default function DocPage({ lang, path }: { lang: Lang; path: string }) {
  const { t } = useTranslation()
  const markdown = getContent(lang, path)
  const articleRef = useRef<HTMLElement>(null)

  // Reset scroll on navigation. The document is the scroller; an inner scroller would break
  // `scroll-padding-top` for TOC jumps and deep links.
  useEffect(() => {
    window.scrollTo({ top: 0, behavior: 'instant' })
  }, [path, lang])

  if (!markdown) {
    return (
      <div className="mx-auto max-w-3xl px-6 py-16 text-center">
        <h1 className="text-2xl font-bold">{t('ui.notFoundTitle')}</h1>
        <p className="mt-2 text-on-surface-variant">
          <Trans
            i18nKey="ui.notFoundBody"
            values={{ path }}
            components={[<code key="path" className="rounded bg-surface-container px-1" />]}
          />
        </p>
        <Link
          to={`/${lang}`}
          className="mt-6 inline-block font-medium text-[var(--np-accent-text)] hover:underline"
        >
          {t('ui.backHome')}
        </Link>
      </div>
    )
  }

  const { prev, next } = neighbors(path)
  // The content came from the fallback language; show a notice rather than serving English
  // text under a translated navigation.
  const fellBack = !hasTranslation(lang, path)

  return (
    <div className="flex w-full">
      {/* Main content column */}
      <div className="mx-auto flex min-w-0 max-w-3xl flex-1 px-6 py-8 lg:px-10">
        <article ref={articleRef} className="np-prose min-w-0 flex-1">
          {fellBack && (
            <div
              lang={DEFAULT_LANG}
              className="mb-6 rounded-lg border border-outline-variant bg-surface-container px-4 py-3 text-sm text-on-surface-variant"
            >
              {t('ui.translationMissing')}
            </div>
          )}

          <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[rehypeSlug, rehypeHighlight]}
            components={makeLinkComponents(lang, path)}
          >
            {markdown}
          </ReactMarkdown>

          <hr className="my-10" />

          <nav className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {prev ? (
              <FooterLink kind="prev" lang={lang} path={prev.path} />
            ) : (
              <span />
            )}
            {next ? <FooterLink kind="next" lang={lang} path={next.path} /> : <span />}
          </nav>
        </article>
      </div>

      {/* Right-side on-this-page TOC (desktop only) */}
      <Toc articleRef={articleRef} path={path} />
    </div>
  )
}

function FooterLink({
  kind,
  lang,
  path,
}: {
  kind: 'prev' | 'next'
  lang: Lang
  path: string
}) {
  const { t } = useTranslation()
  return (
    <Link
      to={`/${lang}/${path}`}
      className={`np-card np-doc-nav group flex flex-col gap-1 px-4 py-3 ${
        kind === 'next' ? 'sm:text-right' : ''
      }`}
    >
      <span
        className={`flex items-center gap-1 text-xs text-on-surface-variant ${
          kind === 'next' ? 'sm:justify-end' : ''
        }`}
      >
        {kind === 'prev' ? (
          <>
            <ArrowLeft size={14} /> {t('ui.prev')}
          </>
        ) : (
          <>
            {t('ui.next')} <ArrowRight size={14} />
          </>
        )}
      </span>
      <span className="font-medium text-on-surface group-hover:text-[var(--np-accent-text)]">
        {t(navTitleKey(path))}
      </span>
    </Link>
  )
}

// Markdown component overrides. Headings keep the `id` from rehype-slug but render no hash
// anchors: clicking `#id` would collide with the HashRouter route in location.hash and land
// on a 404. Section navigation goes through the right-side <Toc/> via scrollIntoView.
//
// Internal cross-links (`./x`, `../group/page`, `/group/page`) become react-router `<Link>`s
// so they navigate client-side. A plain `<a href="./installation">` resolves against the
// document base URL before the `#` and produces a broken `/installation` path.
//
// The markdown sources cross-link by content path only (`../enterprise/folder-rbac`), never
// by language, so the active language is re-applied here to keep a reader in their language.

/** Resolve a markdown cross-link href against the current doc path into a nav
 * path like "getting-started/installation". Returns null for non-internal links. */
function resolveDocHref(href: string, currentPath: string): string | null {
  if (!href) return null
  // External (http/https), mailto, tel, data: leave these to the browser.
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//')) return null
  // In-page anchor (#slug); InternalLink handles it by scrolling.
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

function InternalLink({
  href,
  lang,
  currentPath,
  children,
}: {
  href: string
  lang: Lang
  currentPath: string
  children?: ReactNode
}) {
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
    <Link to={`/${lang}/${target}`}>
      {children}
    </Link>
  )
}

function makeLinkComponents(lang: Lang, currentPath: string): Components {
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
        <InternalLink href={href ?? ''} lang={lang} currentPath={currentPath}>
          {children}
        </InternalLink>
      )
    },
  }
}
