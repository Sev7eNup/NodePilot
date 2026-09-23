import { describe, expect, it } from 'vitest'
import { summarize } from './markdown-summary'
import { contentByLang } from './content'
import { LANGUAGES } from '../i18n/languages'

describe('summarize', () => {
  it('takes the first prose paragraph, not the heading above it', () => {
    expect(summarize('# CLI (np)\n\nThe command line tool.\nIt talks HTTP.\n\nMore.')).toBe(
      'The command line tool. It talks HTTP.',
    )
  })

  it('skips code fences, quotes, tables and lists before the prose', () => {
    const markdown = ['# Title', '', '> A note.', '', '```bash', 'np run', '```', '', '- item', '', 'The real text.'].join(
      '\n',
    )
    expect(summarize(markdown)).toBe('The real text.')
  })

  it('reads links, code spans and emphasis as their text', () => {
    expect(summarize('Use [the CLI](../cli) with `np run` and **care**.')).toBe('Use the CLI with np run and care.')
  })

  it('truncates on a word boundary', () => {
    const summary = summarize(`${'alpha beta '.repeat(40)}end.`, 40)
    expect(summary.length).toBeLessThanOrEqual(40)
    expect(summary.endsWith('…')).toBe(true)
    expect(summary).not.toMatch(/\s…$/)
  })

  it('gives every documentation page a description', () => {
    for (const lang of LANGUAGES) {
      for (const [path, markdown] of Object.entries(contentByLang[lang])) {
        const summary = summarize(markdown)
        expect(summary.length, `${lang}/${path}`).toBeGreaterThan(20)
        expect(summary.length, `${lang}/${path}`).toBeLessThanOrEqual(155)
        // A stray heading or fence would give a description nobody wants in a search result.
        expect(summary, `${lang}/${path}`).not.toMatch(/^[#>|`-]/)
      }
    }
  })
})
