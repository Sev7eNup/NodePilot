import { useCallback, useEffect, useState } from 'react'
import { Routes, Route, useParams, Navigate, useLocation } from 'react-router'
import Sidebar from './components/Sidebar'
import TopBar from './components/TopBar'
import DocPage from './components/DocPage'
import SearchModal from './components/SearchModal'
import { availablePages } from './lib/content'

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
    <div className="np-shell flex min-h-screen text-on-surface">
      <Sidebar
        current={current}
        open={menuOpen}
        onClose={() => setMenuOpen(false)}
        onOpenSearch={openSearch}
      />

      {/* Drawer backdrop — below the rail (z-40), above the sticky TopBar (z-20). */}
      {menuOpen && (
        <button
          type="button"
          aria-label="Navigation schließen"
          onClick={() => setMenuOpen(false)}
          className="fixed inset-0 z-30 bg-black/40 backdrop-blur-sm lg:hidden"
        />
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar current={current} onOpenMenu={() => setMenuOpen(true)} />

        <main className="min-w-0 flex-1">
          <Routes>
            <Route path="/" element={<Navigate to={`/${home}`} replace />} />
            <Route path="*" element={<Page />} />
          </Routes>
        </main>

        <footer className="border-t border-outline-variant/60 px-6 py-5 text-center text-xs text-on-surface-variant">
          NodePilot · agentless Workflow-Orchestrierung für Windows · {new Date().getFullYear()}
        </footer>
      </div>

      <SearchModal open={searchOpen} onClose={() => setSearchOpen(false)} />
    </div>
  )
}
