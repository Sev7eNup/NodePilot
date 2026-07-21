import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { allPages } from '../data/nav'
import { contentMap } from '../lib/content'
import { SearchIcon, CloseIcon, ArrowRightIcon } from '../lib/icons'

interface SearchModalProps {
  open: boolean
  onClose: () => void
}

interface Hit {
  path: string
  title: string
  snippet: string
}

export default function SearchModal({ open, onClose }: SearchModalProps) {
  const [query, setQuery] = useState('')
  const [cursor, setCursor] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const navigate = useNavigate()

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
    const q = query.trim().toLowerCase()
    if (!q) {
      return allPages.slice(0, 8).map((p) => ({
        path: p.path,
        title: p.title,
        snippet: excerpt(contentMap[p.path] ?? ''),
      }))
    }
    const hits: Hit[] = []
    for (const page of allPages) {
      const md = contentMap[page.path]
      if (!md) continue
      const lower = md.toLowerCase()
      const idx = lower.indexOf(q)
      const titleHit = page.title.toLowerCase().includes(q)
      if (idx >= 0 || titleHit) {
        hits.push({
          path: page.path,
          title: page.title,
          snippet: idx >= 0 ? snippetAround(md, idx) : excerpt(md),
        })
      }
      if (hits.length >= 30) break
    }
    return hits
  }, [query])

  useEffect(() => setCursor(0), [query])

  if (!open) return null

  const go = (path: string) => {
    navigate(`/${path}`)
    onClose()
  }

  return (
    <div
      className="fixed inset-0 z-50 flex justify-center bg-black/40 px-4 pt-[12vh] backdrop-blur-sm"
      onClick={onClose}
      role="dialog"
      aria-label="Suche"
    >
      <div
        className="flex h-fit max-h-[60vh] w-full max-w-xl flex-col overflow-hidden rounded-2xl border border-[var(--color-outline-variant)] bg-[var(--color-surface-lowest)] shadow-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-3 border-b border-[var(--color-outline-variant)] px-4">
          <SearchIcon className="h-5 w-5 text-[var(--color-on-surface-variant)]" />
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
            placeholder="Dokumentation durchsuchen…"
            className="h-12 flex-1 bg-transparent text-base outline-none placeholder:text-[var(--color-on-surface-variant)]"
          />
          <button
            type="button"
            onClick={onClose}
            className="grid h-7 w-7 place-items-center rounded-md text-[var(--color-on-surface-variant)] hover:bg-[var(--color-surface-container)]"
            aria-label="Schließen"
          >
            <CloseIcon className="h-4 w-4" />
          </button>
        </div>

        <ul className="np-scroll overflow-y-auto py-2">
          {results.length === 0 && (
            <li className="px-4 py-6 text-center text-sm text-[var(--color-on-surface-variant)]">
              Keine Treffer für „{query}“.
            </li>
          )}
          {results.map((hit, i) => (
            <li key={hit.path}>
              <button
                type="button"
                onMouseEnter={() => setCursor(i)}
                onClick={() => go(hit.path)}
                className={`flex w-full items-start gap-3 px-4 py-2.5 text-left ${
                  i === cursor ? 'bg-[var(--np-accent-soft)]' : 'hover:bg-[var(--color-surface-container)]'
                }`}
              >
                <div className="min-w-0 flex-1">
                  <div
                    className={`truncate text-sm font-medium ${
                      i === cursor ? 'text-[var(--np-accent-text)]' : 'text-[var(--color-on-surface)]'
                    }`}
                  >
                    {hit.title}
                  </div>
                  <div className="truncate text-xs text-[var(--color-on-surface-variant)]">
                    {hit.snippet}
                  </div>
                </div>
                <ArrowRightIcon className="mt-1 h-3.5 w-3.5 shrink-0 text-[var(--color-on-surface-variant)]" />
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