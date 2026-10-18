import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

// Read from disk: vitest replaces CSS imports with an empty string.
const css = readFileSync(new URL('./site.css', import.meta.url), 'utf8')

function tokens(selector) {
  const start = css.indexOf(`\n${selector}{`)
  const block = start < 0 ? '' : css.slice(start, css.indexOf('}', start))
  return [...block.matchAll(/(--[a-z-]+):/g)]
    .map((match) => match[1])
    .filter((name) => !/^--(font|sidebar|header|radius)/.test(name))
    .sort()
}

describe('website themes', () => {
  it('define the same colour tokens for the light and the dark theme', () => {
    const light = tokens(':root')
    expect(light.length).toBeGreaterThan(20)
    expect(tokens('html.dark')).toEqual(light)
  })
})
