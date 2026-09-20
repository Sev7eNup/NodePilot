import { describe, expect, it } from 'vitest'

const legalTexts = import.meta.glob<string>('./legal/*.de.html', { query: '?raw', import: 'default', eager: true })

// The site must not go live without its legal notice and privacy policy. The texts are supplied
// by the operator, so this fails until both files exist.
describe('legal texts', () => {
  it.each(['impressum', 'datenschutz'])('src/site/legal/%s.de.html exists and has content', (name) => {
    const text = legalTexts[`./legal/${name}.de.html`]
    expect(text, `add src/site/legal/${name}.de.html`).toBeDefined()
    expect(text?.trim()).not.toBe('')
  })
})
