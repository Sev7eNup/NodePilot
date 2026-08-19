import { ChevronRight, Menu } from '@carbon/icons-react'
import { useTranslation } from 'react-i18next'
import { groupOf, navGroupKey, navTitleKey, pageByPath } from '../data/nav'

interface TopBarProps {
  /** Active content path, e.g. "getting-started/introduction". */
  current: string
  onOpenMenu: () => void
}

/**
 * Slim chrome over the content column. Brand, search, theme, language and repo link all
 * live in the sidebar (as in the app shell), so this only carries the mobile menu trigger
 * and the breadcrumb.
 */
export default function TopBar({ current, onOpenMenu }: TopBarProps) {
  const { t } = useTranslation()
  const page = pageByPath(current)
  const group = groupOf(current)

  return (
    <header className="sticky top-0 z-20 flex h-12 shrink-0 items-center gap-2 border-b border-outline-variant/40 bg-surface-low/60 px-3 backdrop-blur-sm sm:px-4 lg:px-6">
      <button
        type="button"
        onClick={onOpenMenu}
        className="-ml-1 grid h-8 w-8 shrink-0 place-items-center rounded-lg text-on-surface-variant transition-colors hover:bg-surface-highest hover:text-on-surface lg:hidden"
        aria-label={t('ui.openNav')}
      >
        <Menu size={18} />
      </button>

      <nav aria-label="Breadcrumb" className="flex min-w-0 items-center gap-1.5 text-sm">
        <span className="shrink-0 text-on-surface-variant">{t('ui.breadcrumbRoot')}</span>
        {group && (
          // The group crumb is dropped whole on phones — the page title matters more there.
          <span className="hidden shrink-0 items-center gap-1.5 sm:flex">
            <ChevronRight size={13} className="shrink-0 text-outline/70" />
            <span className="text-on-surface-variant">{t(navGroupKey(group))}</span>
          </span>
        )}
        {page && (
          <>
            <ChevronRight size={13} className="shrink-0 text-outline/70" />
            {/* Not an <h1>: the rendered article already carries the page's markdown H1. */}
            <span aria-current="page" className="truncate font-semibold text-on-surface">
              {t(navTitleKey(page.path))}
            </span>
          </>
        )}
      </nav>
    </header>
  )
}
