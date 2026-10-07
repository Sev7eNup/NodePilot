import { useEffect, useRef } from 'react'
import { Link, useLocation } from 'react-router'
import { Trans, useTranslation } from 'react-i18next'
import { ArrowLeft, ArrowRight } from '@carbon/icons-react'
import DocMarkdown from './DocMarkdown'
import { getContent, hasTranslation } from '../lib/content'
import { navTitleKey, neighbors } from '../data/nav'
import { DEFAULT_LANG, type Lang } from '../i18n/languages'
import { docPath } from '../lib/docPath'
import Toc from './Toc'

export default function DocPage({ lang, path }: { lang: Lang; path: string }) {
  const { t } = useTranslation()
  const markdown = getContent(lang, path)
  const articleRef = useRef<HTMLElement>(null)
  const { hash } = useLocation()

  // Reset scroll on navigation. The document is the scroller; an inner scroller would break
  // `scroll-padding-top` for TOC jumps and deep links.
  useEffect(() => {
    if (hash) {
      try { document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView() } catch { /* Invalid fragment: keep the page usable. */ }
    } else window.scrollTo({ top: 0, behavior: 'instant' })
  }, [path, lang, hash])

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
          to={docPath(lang)}
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

          <DocMarkdown markdown={markdown} lang={lang} path={path} />

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
      to={docPath(lang, path)}
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
