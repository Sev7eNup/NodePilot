/**
 * First prose paragraph of a documentation page, used as its meta description.
 *
 * The pages carry no hand-written descriptions, and 88 of them would go stale the moment the
 * text below them changed. The opening paragraph is what the page is about by construction.
 */

const FENCE = /^(```|~~~)/
const SKIP_LINE = /^(#|>|\||:::|<!--|\s*[-*+]\s|\s*\d+\.\s|\s*$)/

/** Inline markdown that would read as noise in a search result. */
function stripInline(text: string): string {
  return text
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\*\*([^*]*)\*\*/g, '$1')
    .replace(/(^|\s)[*_]([^*_]+)[*_]/g, '$1$2')
    .replace(/<[^>]+>/g, '')
    .replace(/\s+/g, ' ')
    .trim()
}

/** Cuts at the last word boundary that still fits, so no word is left half-written. */
function truncate(text: string, max: number): string {
  if (text.length <= max) return text
  const cut = text.slice(0, max - 1)
  const lastSpace = cut.lastIndexOf(' ')
  return `${(lastSpace > max / 2 ? cut.slice(0, lastSpace) : cut).replace(/[,;:.]$/, '')}…`
}

export function summarize(markdown: string, max = 155): string {
  const lines = markdown.split(/\r?\n/)
  const paragraph: string[] = []
  let fenced = false
  for (const line of lines) {
    if (FENCE.test(line.trim())) {
      fenced = !fenced
      continue
    }
    if (fenced) continue
    if (paragraph.length === 0) {
      if (SKIP_LINE.test(line)) continue
      paragraph.push(line.trim())
      continue
    }
    // A blank line ends the paragraph; so does anything that starts a new block.
    if (line.trim() === '' || SKIP_LINE.test(line)) break
    paragraph.push(line.trim())
  }
  return truncate(stripInline(paragraph.join(' ')), max)
}
