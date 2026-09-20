import { spawnSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { assembleSite } from '../../scripts/assemble-site.mjs'

const packageRoot = fileURLToPath(new URL('../../', import.meta.url))
const FIXTURE_PREFIX = 'np-assemble-site-'

let fixture

function put(path, content) {
  const file = join(fixture, path)
  mkdirSync(dirname(file), { recursive: true })
  writeFileSync(file, content)
}

const read = (path) => readFileSync(join(fixture, path), 'utf8')

/** Runs a copy of the script placed in the fixture, the way `npm run assemble:site` does. */
function runCopiedScript() {
  const script = join(fixture, 'scripts/assemble-site.mjs')
  mkdirSync(dirname(script), { recursive: true })
  copyFileSync(join(packageRoot, 'scripts/assemble-site.mjs'), script)
  return spawnSync(process.execPath, [script], { encoding: 'utf8', windowsHide: true })
}

beforeEach(() => {
  fixture = mkdtempSync(join(tmpdir(), FIXTURE_PREFIX))
  // afterEach deletes this folder recursively, so it has to be the fresh temp fixture.
  if (!resolve(fixture).startsWith(join(resolve(tmpdir()), FIXTURE_PREFIX)))
    throw new Error('Unexpected fixture path')
  put('dist/index.html', 'DOCS_INDEX')
  put('dist/assets/docs.js', 'DOCS_SCRIPT')
  put('dist-site/index.html', 'SITE_INDEX')
  put('dist-site/legacy-docs-redirect.js', 'SITE_REDIRECT')
  put('pages-media/x.mp4', 'TOUR_VIDEO')
  put('public/og-image.png', 'OG_IMAGE')
})

afterEach(() => {
  rmSync(fixture, { recursive: true, force: true })
})

describe('assembleSite', () => {
  it('puts the website at the root, the docs under docs/ and the media under media/', () => {
    const out = assembleSite(fixture, '_site')

    expect(out).toBe(join(fixture, '_site'))
    expect(read('_site/index.html')).toBe('SITE_INDEX')
    expect(read('_site/legacy-docs-redirect.js')).toBe('SITE_REDIRECT')
    expect(read('_site/docs/index.html')).toBe('DOCS_INDEX')
    expect(read('_site/docs/assets/docs.js')).toBe('DOCS_SCRIPT')
    expect(read('_site/media/x.mp4')).toBe('TOUR_VIDEO')
    expect(read('_site/og-image.png')).toBe('OG_IMAGE')
  })

  it('replaces the output of an earlier run', () => {
    put('_site/stale.html', 'STALE')

    assembleSite(fixture, join(fixture, '_site'))

    expect(existsSync(join(fixture, '_site/stale.html'))).toBe(false)
    expect(read('_site/index.html')).toBe('SITE_INDEX')
  })

  it.each([
    ['dist/index.html', /Missing dist\/index\.html\. Run "npm run build" first/],
    ['dist-site/index.html', /Missing dist-site\/index\.html\. Run "npm run build:site" first/],
    ['pages-media', /Missing pages-media/],
    ['public/og-image.png', /Missing public\/og-image\.png/],
  ])('fails with a clear error when %s is missing', (path, message) => {
    rmSync(join(fixture, path), { recursive: true })

    expect(() => assembleSite(fixture, '_site')).toThrow(message)
    expect(existsSync(join(fixture, '_site'))).toBe(false)
  })

  it.each(['docs/index.html', 'media/x.mp4', 'og-image.png'])(
    'refuses a website build containing %s, which the layout takes from another input',
    (path) => {
      put(join('dist-site', path), 'SITE_COPY')

      expect(() => assembleSite(fixture, '_site')).toThrow(/collides with the Pages layout/)
    },
  )

  it.each(['.', 'dist', 'public'])('refuses %s as the output folder', (outDir) => {
    expect(() => assembleSite(fixture, outDir)).toThrow(/Refusing to replace/)
    expect(read('dist/index.html')).toBe('DOCS_INDEX')
    expect(read('public/og-image.png')).toBe('OG_IMAGE')
  })

  it('refuses an output folder outside the package', () => {
    const outside = `${fixture}-outside`
    try {
      expect(() => assembleSite(fixture, outside)).toThrow(/Refusing to replace/)
      expect(existsSync(outside)).toBe(false)
    } finally {
      rmSync(outside, { recursive: true, force: true })
    }
  })

  it('assembles the package that contains the script when run from the command line', () => {
    const result = runCopiedScript()

    expect(result.status, result.stderr).toBe(0)
    expect(result.stdout).toContain('assemble-site: wrote')
    expect(read('_site/index.html')).toBe('SITE_INDEX')
    expect(read('_site/docs/index.html')).toBe('DOCS_INDEX')
    expect(read('_site/media/x.mp4')).toBe('TOUR_VIDEO')
  })

  it('exits non-zero with the reason when run from the command line without a build', () => {
    rmSync(join(fixture, 'dist-site'), { recursive: true })

    const result = runCopiedScript()

    expect(result.status).toBe(1)
    expect(result.stderr).toContain('assemble-site: Missing dist-site/index.html')
  })
})
