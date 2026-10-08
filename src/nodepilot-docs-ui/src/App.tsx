import { useCallback, useEffect, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import Sidebar from './components/Sidebar'
import TopBar from './components/TopBar'
import DocPage from './components/DocPage'
import SearchModal from './components/SearchModal'
import { availablePages } from './lib/content'
import { navTitleKey, pageByPath } from './data/nav'
import { detectLang, parseLocation } from './i18n/languages'
import { docPath } from './lib/docPath'
import { updateDocsHead } from './lib/docs-head'

const FALLBACK_HOME = 'getting-started/introduction'

export default function App() {
  const [menuOpen, setMenuOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const location = useLocation()
  const navigate = useNavigate()
  const { t, i18n } = useTranslation()

  const home = availablePages[0]?.path ?? FALLBACK_HOME
  const { lang, current } = parseLocation(location.pathname)
  const page = current || home

  // A URL without a language prefix redirects to the detected language, keeping the page.
  useEffect(() => {
    if (lang) return
    navigate(`${docPath(detectLang(), current || home)}${location.search}${location.hash}`, { replace: true })
  }, [lang, current, home, navigate, location.search, location.hash])

  // Mirror the URL into i18next and <html lang>. Only the switcher persists a preference.
  useEffect(() => {
    if (!lang) return
    if (i18n.language !== lang) void i18n.changeLanguage(lang)
    document.documentElement.lang = lang
  }, [lang, i18n])

  // Every address is its own prerendered file and carries its own title; navigating inside the
  // page has to keep that true, or the tab and any bookmark keep the title of the page before.
  useEffect(() => {
    if (!lang) return
    updateDocsHead(lang, page, pageByPath(page) ? `${t(navTitleKey(page))}${t('meta.titleSuffix')}` : t('meta.title'))
  }, [lang, page, t])

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
