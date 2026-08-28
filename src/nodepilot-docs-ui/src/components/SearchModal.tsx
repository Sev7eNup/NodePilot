import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { allPages, navTitleKey } from '../data/nav'
import { contentByLang } from '../lib/content'
import { DEFAULT_LANG, type Lang } from '../i18n/languages'
import { ArrowRight, Close, Search } from '@carbon/icons-react'

interface SearchModalProps {
  /** Active language: selects the corpus that is searched and the routes that are built. */
  lang: Lang
  open: boolean
  onClose: () => void
}

interface Hit {
  path: string
  title: string
  snippet: string
}

export default function SearchModal({ lang, open, onClose }: SearchModalProps) {
  const [query, setQuery] = useState('')
  const [cursor, setCursor] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const navigate = useNavigate()
  const { t } = useTranslation()

  useEffect(() => {
    if (open) {
      setQuery('')
      setCursor(0)
      // Defer focus until the input mounts.
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }, [open])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    if (open) window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const results: Hit[] = useMemo(() => {
    // Search the reader's language, falling back per page so an untranslated page stays
    // findable.
    const corpus = (path: string) =>
      contentByLang[lang]?.[path] ?? contentByLang[DEFAULT_LANG]?.[path]

    const q = query.trim().toLowerCase()
    if (!q) {
      return allPages.slice(0, 8).map((p) => ({
        path: p.path,
        title: t(navTitleKey(p.path)),
        snippet: excerpt(corpus(p.path) ?? ''),
      }))
    }
    const hits: Hit[] = []
    for (const page of allPages) {
      const md = corpus(page.path)
      if (!md) continue
      const title = t(navTitleKey(page.path))
      const lower = md.toLowerCase()
      const idx = lower.indexOf(q)
      const titleHit = title.toLowerCase().includes(q)
      if (idx >= 0 || titleHit) {
        hits.push({
          path: page.path,
          title,
          snippet: idx >= 0 ? snippetAround(md, idx) : excerpt(md),
        })
      }
      if (hits.length >= 30) break
    }
    return hits
  }, [query, lang, t])

  useEffect(() => setCursor(0), [query])

  if (!open) return null

  const go = (path: string) => {
    navigate(`/${lang}/${path}`)
    onClose()
  }

  return (
    <div
      className="fixed inset-0 z-50 flex justify-center bg-black/40 px-4 pt-[12vh] backdrop-blur-sm"
      onClick={onClose}
      role="dialog"
      aria-label={t('ui.search')}
    >
      {/* `np-card` carries the lit-plate treatment and its own dark-mode shadow; adding a
          `shadow-2xl` utility here would be a second rule conflicting with it. */}
      <div
        className="np-card flex h-fit max-h-[60vh] w-full max-w-xl flex-col overflow-hidden"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-3 border-b border-outline-variant px-4">
          <Search size={20} className="shrink-0 text-on-surface-variant" />
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') {
                e.preventDefault()
                setCursor((c) => Math.min(c + 1, results.length - 1))
              } else if (e.key === 'ArrowUp') {
                e.preventDefault()
                setCursor((c) => Math.max(c - 1, 0))
              } else if (e.key === 'Enter' && results[cursor]) {
                e.preventDefault()
                go(results[cursor].path)
              }
            }}
            placeholder={t('ui.searchPlaceholder')}
            className="h-12 min-w-0 flex-1 bg-transparent text-base outline-none placeholder:text-on-surface-variant"
          />
          <button
            type="button"
            onClick={onClose}
            className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-on-surface-variant hover:bg-surface-container hover:text-on-surface"
            aria-label={t('ui.close')}
          >
            <Close size={16} />
          </button>
        </div>

        <ul className="overflow-y-auto py-2">
          {results.length === 0 && (
            <li className="px-4 py-6 text-center text-sm text-on-surface-variant">
              {t('ui.noResults', { query })}
            </li>
          )}
          {results.map((hit, i) => (
            <li key={hit.path}>
              <button
                type="button"
                onMouseEnter={() => setCursor(i)}
                onClick={() => go(hit.path)}
                className={`flex w-full items-start gap-3 px-4 py-2.5 text-left ${
                  i === cursor ? 'bg-[var(--np-accent-soft)]' : 'hover:bg-surface-container'
                }`}
              >
                <div className="min-w-0 flex-1">
                  <div
                    className={`truncate text-sm font-medium ${
                      i === cursor ? 'text-[var(--np-accent-text)]' : 'text-on-surface'
                    }`}
                  >
                    {hit.title}
                  </div>
                  <div className="truncate text-xs text-on-surface-variant">
                    {hit.snippet}
                  </div>
                </div>
                <ArrowRight size={14} className="mt-1 shrink-0 text-on-surface-variant" />
              </button>
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}

function excerpt(md: string): string {
  const lines = md.split('\n').filter((l) => l.trim() && !l.startsWith('#'))
  return lines.slice(0, 1).join(' ').slice(0, 120)
}

function snippetAround(md: string, idx: number): string {
  const start = Math.max(0, idx - 50)
  const end = Math.min(md.length, idx + 80)
  const snip = md
    .slice(start, end)
    .replace(/\n/g, ' ')
    .replace(/[#`*>]/g, '')
    .trim()
  return `${start > 0 ? '… ' : ''}${snip}${end < md.length ? ' …' : ''}`
}
