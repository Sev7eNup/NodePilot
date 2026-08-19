import { useState } from 'react'
import { NavLink } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Asleep, ChevronRight, Close, Download, Light, LogoGithub, Search } from '@carbon/icons-react'
import { navGroups, navGroupKey, navTitleKey, type NavPage } from '../data/nav'
import { useTheme } from '../lib/useTheme'
import LanguageSwitcher from './LanguageSwitcher'
import type { Lang } from '../i18n/languages'
import logoLight from '../assets/logo-light.png'
import logoDark from '../assets/logo-dark.png'

interface SidebarProps {
  /** Active language — every nav link is minted under it. */
  lang: Lang
  /** Active content path, e.g. "getting-started/introduction". */
  current: string
  /** Drawer state — only meaningful below `lg`, where the same <aside> is the off-canvas drawer. */
  open: boolean
  onClose: () => void
  onOpenSearch: () => void
}

/**
 * The full-height navigation rail, ported from the app shell's sidebar. One <aside>
 * serves both layouts: a sticky 292px rail from `lg` up, and an off-canvas drawer below
 * it. The mobile branch is written with `max-lg:` rather than `lg:` overrides on purpose —
 * a `translate` that survives into the desktop layout would make the element a containing
 * block for `position: fixed` descendants and its own stacking context.
 */
export default function Sidebar({ lang, current, open, onClose, onOpenSearch }: SidebarProps) {
  const { theme, toggle } = useTheme()
  const { t } = useTranslation()
  const logo = theme === 'dark' ? logoDark : logoLight

  return (
    <aside
      className={`np-sidebar np-sidebar-expanded flex flex-col
        max-lg:fixed max-lg:inset-y-0 max-lg:left-0 max-lg:z-40 max-lg:w-72
        max-lg:duration-200 max-lg:ease-in-out
        ${open ? 'max-lg:translate-x-0' : 'is-closed max-lg:invisible max-lg:-translate-x-full'}
        lg:sticky lg:top-0 lg:h-screen lg:shrink-0`}
    >
      {/* Brand */}
      <div className="flex items-start gap-2 px-[18px] pt-[22px] pb-0">
        <div className="flex min-w-0 flex-1 items-center gap-3">
          <div className="np-brand-mark grid h-[42px] w-[42px] shrink-0 place-items-center rounded-[13px]">
            <img src={logo} alt="" aria-hidden="true" className="h-6 w-6 select-none" draggable={false} />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="truncate bg-gradient-to-r from-primary to-primary-container bg-clip-text text-[16px] font-bold leading-none text-transparent">
                NodePilot
              </span>
              <span className="np-brand-edition shrink-0">{t('ui.brandEdition')}</span>
            </div>
            <p className="mt-1 truncate text-[10px] font-semibold uppercase leading-none tracking-[0.09em] text-on-surface-variant/70">
              {t('ui.tagline')}
            </p>
          </div>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="-mr-1 shrink-0 rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-highest hover:text-on-surface lg:hidden"
          aria-label={t('ui.closeNav')}
        >
          <Close size={18} />
        </button>
      </div>

      {/* Search — a button, not an input: it opens the existing search modal, and a
          focus-triggered input would fight Ctrl-K for the focus. */}
      <div className="px-[18px] pb-4 pt-5">
        <div className="relative">
          <Search
            size={14}
            className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant/60"
          />
          <button type="button" onClick={onOpenSearch} className="np-sb-search-input">
            {t('ui.searchPlaceholder')}
          </button>
          <span className="np-sb-search-kbd pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 select-none">
            {t('ui.searchShortcut')}
          </span>
        </div>
      </div>

      {/* Nav — scrolls on its own so the footer controls stay pinned. */}
      <nav className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden px-3 pb-[22px] pt-1">
        {navGroups.map((group, gi) => (
          <SidebarGroup
            key={group.id}
            id={group.id}
            items={group.items}
            lang={lang}
            current={current}
            first={gi === 0}
            onNavigate={onClose}
          />
        ))}
      </nav>

      {/* Download call-to-action. A reader who has understood the docs needs somewhere to go
          next, and `/releases/latest` is the one GitHub URL that always resolves to the newest
          release. It cannot be a fourth button in the footer row below: `.np-skin-btn` is a
          fixed 40x38 icon cell with no room for a word. */}
      <div className="px-[18px] pb-4">
        <a
          href="https://github.com/Sev7eNup/NodePilot/releases/latest"
          target="_blank"
          rel="noreferrer"
          className="np-sb-download"
        >
          <Download size={16} aria-hidden />
          <span>{t('ui.download')}</span>
        </a>
      </div>

      {/* Footer — theme toggle, language and repo link. Extra bottom padding clears the
          phone's system navigation bar (0 on desktop). */}
      <div className="flex items-center gap-2 border-t border-outline-variant/60 px-3 pb-[calc(0.75rem_+_env(safe-area-inset-bottom))] pt-3">
        <button
          type="button"
          onClick={toggle}
          className="np-skin-btn"
          aria-label={theme === 'dark' ? t('ui.lightMode') : t('ui.darkMode')}
          title={theme === 'dark' ? t('ui.lightMode') : t('ui.darkMode')}
        >
          {theme === 'dark' ? <Light size={16} /> : <Asleep size={16} />}
        </button>
        <LanguageSwitcher lang={lang} current={current} />
        <a
          href="https://github.com/Sev7eNup/NodePilot"
          target="_blank"
          rel="noreferrer"
          className="np-skin-btn"
          aria-label={t('ui.github')}
          title="GitHub"
        >
          <LogoGithub size={16} />
        </a>
      </div>
    </aside>
  )
}

function SidebarGroup({
  id,
  items,
  lang,
  current,
  first,
  onNavigate,
}: {
  id: string
  items: NavPage[]
  lang: Lang
  current: string
  first: boolean
  onNavigate: () => void
}) {
  const { t } = useTranslation()
  // Default: the group holding the current page is open, so arriving via search or
  // prev/next always reveals the active item. A manual toggle wins from then on.
  const [override, setOverride] = useState<boolean | null>(null)
  const open = override ?? items.some((i) => i.path === current)

  return (
    <div className={first ? 'mt-1.5' : 'mt-[18px]'}>
      {/* Disclosure button. No `aria-controls` on purpose — the panel is conditionally
          rendered, so the id it would name does not exist while collapsed. */}
      <button
        type="button"
        onClick={() => setOverride(!open)}
        className="np-sb-section-title mb-[5px]"
        aria-expanded={open}
      >
        <span>{t(navGroupKey(id))}</span>
        <ChevronRight
          size={12}
          className={`shrink-0 transition-transform ${open ? 'rotate-90' : ''}`}
        />
      </button>
      {open && (
        <div className="grid gap-[3px]">
          {items.map(({ path, icon: Icon }) => (
            <NavLink key={path} to={`/${lang}/${path}`} end onClick={onNavigate} className="np-nav">
              <span className="np-nav-icon">
                <Icon size={18} aria-hidden />
              </span>
              <span>{t(navTitleKey(path))}</span>
            </NavLink>
          ))}
        </div>
      )}
    </div>
  )
}
