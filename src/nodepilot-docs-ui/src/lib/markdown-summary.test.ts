import { describe, expect, it } from 'vitest'
import { summarize } from './markdown-summary'
import { contentByLang } from './content'
import { LANGUAGES } from '../i18n/languages'

describe('summarize', () => {
  it('takes the first prose paragraph, not the heading above it', () => {
    const long = 'The command line tool talks HTTP to the NodePilot API and covers every operation the web UI offers.'
    expect(summarize(`# CLI (np)\n\n${long}\nIt uses the same login.\n\nMore.`)).toBe(`${long} It uses the same login.`)
  })

  it('adds the next paragraph when the first one is very short', () => {
    expect(summarize('# CLI\n\nThe command line tool.\n\nIt talks HTTP.')).toBe('The command line tool. It talks HTTP.')
  })

  it('keeps adding below an opening too short for a search snippet', () => {
    const opening = 'An edge connects two nodes. A condition determines when the target node is executed.'
    expect(summarize(`${opening}

- On success.
- On failure.

## Next`)).toBe(`${opening} On success. On failure.`)
  })

  it('takes a one-line sketch after a lead-in, but not a real code block', () => {
    expect(summarize('Creates a minimal workflow:\n\n```text\nTrigger -> Script\n```\n\nMore.')).toBe('Creates a minimal workflow: Trigger -> Script. More.')
    expect(summarize('Run this:\n\n```powershell\nGet-Service\nGet-Process\n```')).toBe('Run this:')
  })

  it('names the first column of a table that a lead-in announces', () => {
    const markdown = ['Logs go through Serilog. The format is set by Logging:Format:', '', '| Value | Use |', '|---|---|', '| `text` | Console |', '| `json` | Ingest |', '', '## Files'].join('\n')
    expect(summarize(markdown)).toBe('Logs go through Serilog. The format is set by Logging:Format: text, json.')
  })

  it('skips code fences, quotes, tables and lists before the prose', () => {
    const markdown = ['# Title', '', '> A note.', '', '```bash', 'np run', '```', '', '- item', '', 'The real text.'].join(
      '\n',
    )
    expect(summarize(markdown)).toBe('The real text.')
  })

  it('continues a lead-in that ends in a colon with the list below it', () => {
    const markdown = ['# Export', '', 'NodePilot uses two export formats:', '', '- **Workflow export** for sharing.', '- **System backup** for restore.', '', '## Next'].join('\n')
    expect(summarize(markdown)).toBe('NodePilot uses two export formats: Workflow export for sharing. System backup for restore.')
  })

  it('stops at the next heading even when the text is still short', () => {
    expect(summarize('Short intro.\n\n## Section\n\nOther text.')).toBe('Short intro.')
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
        // Shorter than this, a result snippet says little and SEO checks flag it.
        expect(summary.length, `${lang}/${path}`).toBeGreaterThanOrEqual(120)
        expect(summary.length, `${lang}/${path}`).toBeLessThanOrEqual(155)
        // A stray heading or fence would give a description nobody wants in a search result.
        expect(summary, `${lang}/${path}`).not.toMatch(/^[#>|`-]/)
        // A description that stops at a colon promises text the snippet never shows.
        expect(summary, `${lang}/${path}`).not.toMatch(/:$/)
      }
    }
  })
})
