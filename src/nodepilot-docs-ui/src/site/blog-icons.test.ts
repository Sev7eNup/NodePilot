import { describe, expect, it } from 'vitest'
import { articles } from './blog'
import { articleIcon, articleIcons } from './blog-icons'

describe('blog list icons', () => {
  it('gives every catalog article an icon of its own', () => {
    for (const article of articles) expect(articleIcons[article.slug], article.slug).toBeDefined()
  })

  it('renders a decorative Carbon icon', () => {
    const svg = articleIcon('winrm', 'PRAXIS')
    expect(svg).toMatch(/^<svg class="icon" [^>]*viewBox="0 0 32 32"[^>]*aria-hidden="true"/)
    expect(svg).toContain('<path ')
  })

  it('falls back to the category icon for an article without an entry', () => {
    expect(articleIcon('not-in-the-list', 'HINTERGRUND')).toBe(articleIcon('why-nodepilot-unlisted', 'HINTERGRUND'))
    expect(articleIcon('not-in-the-list', 'HINTERGRUND')).not.toBe(articleIcon('not-in-the-list', 'PRAXIS'))
  })
})
