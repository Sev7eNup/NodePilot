import { useEffect, useState } from 'react'
import faviconDark from '../assets/logo-dark.png'
import faviconLight from '../assets/logo-light.png'

type Theme = 'light' | 'dark'

/**
 * Light/dark toggle. The initial resolution (stored preference → OS preference) happens
 * in the inline pre-hydration script in `index.html`, which has already stamped
 * `html.dark` before first paint; seeding from that class here keeps one resolution rule
 * instead of two that can drift.
 */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(() =>
    document.documentElement.classList.contains('dark') ? 'dark' : 'light',
  )

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
    localStorage.setItem('np-docs-theme', theme)

    const favicon = document.querySelector<HTMLLinkElement>('#docs-favicon')
    if (favicon) favicon.href = theme === 'dark' ? faviconDark : faviconLight
  }, [theme])

  return { theme, toggle: () => setTheme((t) => (t === 'dark' ? 'light' : 'dark')) }
}
