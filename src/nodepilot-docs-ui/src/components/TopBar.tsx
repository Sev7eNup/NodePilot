import { Link } from 'react-router-dom'
import { useTheme } from '../lib/useTheme'
import { SunIcon, MoonIcon, SearchIcon, MenuIcon } from '../lib/icons'
import logoLight from '../assets/logo-light.png'
import logoDark from '../assets/logo-dark.png'

interface TopBarProps {
  onOpenSearch: () => void
  onOpenMenu: () => void
}

export default function TopBar({ onOpenSearch, onOpenMenu }: TopBarProps) {
  const { theme, toggle } = useTheme()

  return (
    <header className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b border-[var(--color-outline-variant)] bg-[var(--color-surface)]/85 px-4 backdrop-blur-md">
      <button
        type="button"
        onClick={onOpenMenu}
        className="grid h-9 w-9 place-items-center rounded-lg text-[var(--color-on-surface-variant)] hover:bg-[var(--color-surface-container)] lg:hidden"
        aria-label="Navigation öffnen"
      >
        <MenuIcon />
      </button>

      <Link to="/" className="flex items-center gap-2.5">
        <Logo theme={theme} />
        <span className="text-base font-bold tracking-tight">NodePilot</span>
        <span className="hidden text-xs font-normal text-[var(--color-on-surface-variant)] sm:inline">
          · Docs
        </span>
      </Link>

      <div className="flex-1" />

      <button
        type="button"
        onClick={onOpenSearch}
        className="flex h-9 items-center gap-2 rounded-lg border border-[var(--color-outline-variant)] bg-[var(--color-surface-lowest)] px-3 text-sm text-[var(--color-on-surface-variant)] transition-colors hover:border-[var(--np-accent-ring)] hover:text-[var(--color-on-surface)] sm:w-64 sm:justify-between"
      >
        <span className="flex items-center gap-2">
          <SearchIcon className="h-4 w-4" />
          <span className="hidden sm:inline">Suchen…</span>
        </span>
        <kbd className="hidden rounded border border-[var(--color-outline-variant)] bg-[var(--color-surface-container)] px-1.5 py-0.5 font-mono text-[0.65rem] sm:inline">
          Strg K
        </kbd>
      </button>

      <button
        type="button"
        onClick={toggle}
        className="grid h-9 w-9 place-items-center rounded-lg border border-[var(--color-outline-variant)] text-[var(--color-on-surface-variant)] transition-colors hover:border-[var(--np-accent-ring)] hover:text-[var(--color-on-surface)]"
        aria-label={theme === 'dark' ? 'Light-Modus' : 'Dark-Modus'}
        title={theme === 'dark' ? 'Light-Modus' : 'Dark-Modus'}
      >
        {theme === 'dark' ? <SunIcon /> : <MoonIcon />}
      </button>

      <a
        href="https://github.com/Sev7eNup/NodePilot"
        target="_blank"
        rel="noreferrer"
        className="hidden h-9 w-9 place-items-center rounded-lg border border-[var(--color-outline-variant)] text-[var(--color-on-surface-variant)] transition-colors hover:border-[var(--np-accent-ring)] hover:text-[var(--color-on-surface)] sm:grid"
        aria-label="GitHub-Repository"
        title="GitHub"
      >
        <GithubGlyph />
      </a>
    </header>
  )
}

function Logo({ theme }: Readonly<{ theme: 'light' | 'dark' }>) {
  // NodePilot brand mark — the workflow-graph app icon, matched to the docs accent
  // like the main app's BrandLogo: blue in light mode, orange in dark mode.
  return (
    <img
      src={theme === 'dark' ? logoDark : logoLight}
      alt=""
      aria-hidden="true"
      className="h-7 w-7 select-none"
      draggable={false}
    />
  )
}

function GithubGlyph() {
  return (
    <svg viewBox="0 0 24 24" className="h-4 w-4" fill="currentColor" aria-hidden="true">
      <path d="M12 .5C5.37.5 0 5.78 0 12.29c0 5.21 3.44 9.62 8.21 11.18.6.11.82-.25.82-.56v-2.02c-3.34.71-4.04-1.59-4.04-1.59-.55-1.37-1.34-1.74-1.34-1.74-1.09-.73.08-.72.08-.72 1.21.08 1.85 1.22 1.85 1.22 1.07 1.8 2.81 1.28 3.5.98.11-.76.42-1.28.76-1.58-2.67-.3-5.47-1.31-5.47-5.83 0-1.29.47-2.34 1.23-3.17-.12-.3-.53-1.51.12-3.15 0 0 1.01-.32 3.3 1.21a11.6 11.6 0 0 1 6 0c2.29-1.53 3.3-1.21 3.3-1.21.65 1.64.24 2.85.12 3.15.77.83 1.23 1.88 1.23 3.17 0 4.53-2.81 5.53-5.49 5.82.43.37.81 1.1.81 2.22v3.29c0 .31.22.68.83.56A12.04 12.04 0 0 0 24 12.29C24 5.78 18.63.5 12 .5z" />
    </svg>
  )
}