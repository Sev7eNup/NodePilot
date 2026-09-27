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

const LIST_ITEM = /^\s*(?:[-*+]|\d+\.)\s+/
const SECTION_END = /^(#|>|\||:::)/

/** A lead-in ending in a colon, or a very short opening, says too little on its own. */
function needsMore(text: string): boolean {
  return text.endsWith(':') || text.length < 90
}

export function summarize(markdown: string, max = 155): string {
  const lines = markdown.split(/\r?\n/)
  const paragraph: string[] = []
  let fenced = false
  let index = 0
  for (; index < lines.length; index++) {
    const line = lines[index]
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
  let text = stripInline(paragraph.join(' '))
  // Continue with the prose or list right below, up to the next heading or code block. A table
  // contributes its first column, which names what the lead-in announced.
  for (; needsMore(text) && index < lines.length && text.length < 120; index++) {
    const line = lines[index]
    if (line.startsWith('|')) {
      const end = lines.findIndex((row, i) => i > index && !row.startsWith('|'))
      const cells = lines.slice(index + 2, end < 0 ? undefined : end).map((row) => stripInline(row.split('|')[1] ?? '')).filter(Boolean)
      if (cells.length) text = `${text} ${cells.join(', ')}.`
      break
    }
    if (FENCE.test(line.trim())) {
      // A one-line sketch such as `Trigger -> Script` reads as prose; real code does not.
      const end = lines.findIndex((row, i) => i > index && FENCE.test(row.trim()))
      const code = lines.slice(index + 1, end).join(' ').trim()
      if (end < 0 || end - index !== 2 || code.length > 80) break
      text = `${text} ${code}.`
      index = end
      continue
    }
    if (SECTION_END.test(line)) break
    const part = stripInline(line.replace(LIST_ITEM, ''))
    if (part) text = `${text} ${part}`
  }
  return truncate(text, max)
}
