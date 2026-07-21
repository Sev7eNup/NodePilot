import { useEffect, useRef, useState } from 'react'
import type { RefObject } from 'react'

interface Heading {
  id: string
  text: string
  level: number
}

/**
 * Right-side "Auf dieser Seite" navigation.
 *
 * Headings are read from the **rendered DOM** (the article's `h2[id]`/`h3[id]`,
 * whose ids come from `rehype-slug` → `github-slugger`) rather than recomputed
 * from the markdown source. This guarantees the jump target's id exactly matches
 * the rendered element, even for headings with `&` / `/` / `·` separators where
 * a hand-rolled slugify would diverge from github-slugger (single vs. double hyphen).
 */
export default function Toc({
  articleRef,
  path,
}: {
  articleRef: RefObject<HTMLElement | null>
  path: string
}) {
  const [headings, setHeadings] = useState<Heading[]>([])
  const [active, setActive] = useState<string | undefined>(undefined)
  const observerRef = useRef<IntersectionObserver | null>(null)

  // Re-extract headings whenever the page changes.
  useEffect(() => {
    const root = articleRef.current
    if (!root) {
      setHeadings([])
      return
    }
    const els = Array.from(root.querySelectorAll<HTMLElement>('h2[id], h3[id]'))
    const hs = els.map((el) => ({
      id: el.id,
      text: el.textContent ?? '',
      level: el.tagName === 'H2' ? 2 : 3,
    }))
    setHeadings(hs)
    setActive(hs[0]?.id)
  }, [articleRef, path])

  // Track the active section via IntersectionObserver.
  useEffect(() => {
    observerRef.current?.disconnect()
    if (headings.length === 0) return
    const obs = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((e) => e.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)
        if (visible[0]) setActive(visible[0].target.id)
      },
      { rootMargin: '-80px 0px -70% 0px', threshold: [0, 1] },
    )
    observerRef.current = obs
    headings.forEach((h) => {
      const el = document.getElementById(h.id)
      if (el) obs.observe(el)
    })
    return () => obs.disconnect()
  }, [headings])

  if (headings.length === 0) return null

  return (
    <aside className="sticky top-20 hidden h-[calc(100vh-6rem)] w-64 shrink-0 overflow-y-auto pr-6 pt-8 xl:block">
      <p className="mb-2 text-xs font-semibold uppercase tracking-wider text-[var(--color-on-surface-variant)]">
        Auf dieser Seite
      </p>
      <ul className="border-l border-[var(--color-outline-variant)] text-sm">
        {headings.map((h) => (
          <li key={h.id} className="-ml-px">
            <button
              type="button"
              onClick={() =>
                document.getElementById(h.id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
              }
              className={`-ml-px block w-full border-l-2 py-1 text-left transition-colors ${
                h.level === 3 ? 'pl-6' : 'pl-3'
              } ${
                active === h.id
                  ? 'border-[var(--np-accent)] text-[var(--np-accent-text)]'
                  : 'border-transparent text-[var(--color-on-surface-variant)] hover:text-[var(--color-on-surface)]'
              }`}
            >
              {h.text}
            </button>
          </li>
        ))}
      </ul>
    </aside>
  )
}