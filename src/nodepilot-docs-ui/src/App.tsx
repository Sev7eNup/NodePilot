import { useCallback, useEffect, useState } from 'react'
import { Routes, Route, useParams, Navigate, useLocation } from 'react-router-dom'
import Sidebar from './components/Sidebar'
import TopBar from './components/TopBar'
import DocPage from './components/DocPage'
import SearchModal from './components/SearchModal'
import { availablePages } from './lib/content'
import { CloseIcon } from './lib/icons'

/** Reads the catch-all route segment (React Router's `*` "splat") and turns it into a
 *  single lookup key, e.g. `getting-started/introduction` or `cli`. */
function useDocPath(): string {
  const params = useParams()
  const splat = (params['*'] ?? '').replace(/^\//, '')
  return splat
}

function Page() {
  const path = useDocPath()
  return <DocPage path={path} />
}

export default function App() {
  const [menuOpen, setMenuOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const location = useLocation()

  const current = location.pathname.replace(/^\//, '')

  // Close mobile drawer on navigation.
  useEffect(() => setMenuOpen(false), [location.pathname])

  // Global Ctrl/Cmd+K → search.
  const openSearch = useCallback(() => setSearchOpen(true), [])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setSearchOpen((v) => !v)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const home = availablePages[0]?.path ?? 'getting-started/introduction'

  return (
    <div className="flex min-h-screen flex-col bg-[var(--color-surface)] text-[var(--color-on-surface)]">
      <TopBar onOpenSearch={openSearch} onOpenMenu={() => setMenuOpen(true)} />

      <div className="mx-auto flex w-full max-w-[1400px] flex-1">
        {/* Desktop sidebar */}
        <aside className="sticky top-14 hidden h-[calc(100vh-3.5rem)] w-64 shrink-0 border-r border-[var(--color-outline-variant)] bg-[var(--color-surface-low)] px-3 py-4 lg:block">
          <Sidebar current={current} />
        </aside>

        {/* Mobile drawer */}
        {menuOpen && (
          <div className="fixed inset-0 z-40 lg:hidden">
            <div
              className="absolute inset-0 bg-black/40 backdrop-blur-sm"
              onClick={() => setMenuOpen(false)}
            />
            <div className="absolute left-0 top-0 h-full w-80 max-w-[85vw] overflow-y-auto bg-[var(--color-surface-low)] p-4 shadow-xl">
              <div className="mb-3 flex items-center justify-between">
                <span className="text-sm font-semibold">Navigation</span>
                <button
                  type="button"
                  onClick={() => setMenuOpen(false)}
                  className="grid h-8 w-8 place-items-center rounded-lg text-[var(--color-on-surface-variant)] hover:bg-[var(--color-surface-container)]"
                  aria-label="Schließen"
                >
                  <CloseIcon />
                </button>
              </div>
              <Sidebar current={current} onNavigate={() => setMenuOpen(false)} />
            </div>
          </div>
        )}

        <main className="min-w-0 flex-1">
          <Routes>
            <Route path="/" element={<Navigate to={`/${home}`} replace />} />
            <Route path="*" element={<Page />} />
          </Routes>
        </main>
      </div>

      <SearchModal open={searchOpen} onClose={() => setSearchOpen(false)} />

      <footer className="border-t border-[var(--color-outline-variant)] bg-[var(--color-surface-low)] px-6 py-5 text-center text-xs text-[var(--color-on-surface-variant)]">
        NodePilot · agentless Workflow-Orchestrierung für Windows · {new Date().getFullYear()}
      </footer>
    </div>
  )
}