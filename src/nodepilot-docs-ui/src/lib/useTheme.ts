import { useEffect, useState } from 'react'
import faviconDark from '../assets/logo-dark.png'
import faviconLight from '../assets/logo-light.png'

type Theme = 'light' | 'dark'

/**
 * Light/dark toggle. The initial choice (stored preference, else OS preference) is made by
 * the inline pre-hydration script in `index.html`, which stamps `html.dark` before first
 * paint. Seeding from that class keeps a single resolution rule instead of two that can drift.
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
