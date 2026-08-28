import { useCallback, useEffect, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import Sidebar from './components/Sidebar'
import TopBar from './components/TopBar'
import DocPage from './components/DocPage'
import SearchModal from './components/SearchModal'
import { availablePages } from './lib/content'
import { detectLang, LANG_STORAGE_KEY, parseLocation } from './i18n/languages'

const FALLBACK_HOME = 'getting-started/introduction'

export default function App() {
  const [menuOpen, setMenuOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const location = useLocation()
  const navigate = useNavigate()
  const { t, i18n } = useTranslation()

  const home = availablePages[0]?.path ?? FALLBACK_HOME
  const { lang, current } = parseLocation(location.pathname)

  // A URL without a language prefix redirects to the detected language, keeping the page.
  useEffect(() => {
    if (lang) return
    navigate(`/${detectLang()}/${current || home}`, { replace: true })
  }, [lang, current, home, navigate])

  // The URL is authoritative for the language: mirror it into i18next, remember the choice
  // for the next visit, and set <html lang> for screen readers and search engines.
  useEffect(() => {
    if (!lang) return
    if (i18n.language !== lang) void i18n.changeLanguage(lang)
    document.documentElement.lang = lang
    try {
      window.localStorage?.setItem(LANG_STORAGE_KEY, lang)
    } catch {
      // Private mode or disabled storage; the language stays in the URL.
    }
  }, [lang, i18n])

  // Close mobile drawer on navigation.
  useEffect(() => setMenuOpen(false), [location.pathname])

  // Global Ctrl/Cmd+K toggles the search modal.
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

  // While redirecting, rendering the shell would flash the navigation in the wrong language.
  if (!lang) return null

  const page = current || home

  return (
    <div className="np-shell flex min-h-screen text-on-surface">
      <Sidebar
        lang={lang}
        current={page}
        open={menuOpen}
        onClose={() => setMenuOpen(false)}
        onOpenSearch={openSearch}
      />

      {/* Drawer backdrop: below the rail (z-40), above the sticky TopBar (z-20). */}
      {menuOpen && (
        <button
          type="button"
          aria-label={t('ui.closeNav')}
          onClick={() => setMenuOpen(false)}
          className="fixed inset-0 z-30 bg-black/40 backdrop-blur-sm lg:hidden"
        />
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar current={page} onOpenMenu={() => setMenuOpen(true)} />

        <main className="min-w-0 flex-1">
          <DocPage lang={lang} path={page} />
        </main>

        <footer className="border-t border-outline-variant/60 px-6 py-5 text-center text-xs text-on-surface-variant">
          {t('ui.footer', { year: new Date().getFullYear() })}
        </footer>
      </div>

      <SearchModal lang={lang} open={searchOpen} onClose={() => setSearchOpen(false)} />
    </div>
  )
}
