import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { assembleSite } from '../../scripts/assemble-site.mjs'

const packageRoot = fileURLToPath(new URL('../../', import.meta.url))
const repoRoot = resolve(packageRoot, '../..')

describe('Pages-only media packaging', () => {
  it('keeps the tour available to Pages outside the installer public directory', () => {
    for (const name of ['nodepilot-product-tour.mp4', 'product-tour-poster.png']) {
      expect(existsSync(join(packageRoot, 'pages-media', name))).toBe(true)
      expect(existsSync(join(packageRoot, 'public/media', name))).toBe(false)
    }
    const workflow = readFileSync(join(repoRoot, '.github/workflows/docs-pages.yml'), 'utf8')
    expect(workflow).toContain('npm run assemble:site')
    expect(workflow).toContain('path: src/nodepilot-docs-ui/_site')
    const { scripts } = JSON.parse(readFileSync(join(packageRoot, 'package.json'), 'utf8'))
    expect(scripts['assemble:site']).toBe('node scripts/assemble-site.mjs')
  })

  it('publishes pages-media under media/, where the tour URL points', () => {
    const workspace = mkdtempSync(join(tmpdir(), 'np-media-site-'))
    if (!resolve(workspace).startsWith(join(resolve(tmpdir()), 'np-media-site-')))
      throw new Error('Unexpected fixture path')
    try {
      // Mirrors the repo's src/ layout: the browser demo is built in a sibling package.
      const fixture = join(workspace, 'nodepilot-docs-ui')
      const files = {
        // Needs a <head>: assemble-site stamps the published docs copy as the Pages one.
        'dist/index.html': '<!doctype html><html><head></head><body>DOCS</body></html>',
        'dist-site/index.html': 'SITE',
        '../nodepilot-ui/dist-demo/index.html': 'DEMO',
        'pages-media/nodepilot-product-tour.mp4': 'TOUR_VIDEO_SENTINEL',
        'public/og-image.png': 'OG',
      }
      for (const [path, content] of Object.entries(files)) {
        mkdirSync(dirname(join(fixture, path)), { recursive: true })
        writeFileSync(join(fixture, path), content)
      }

      assembleSite(fixture, '_site')

      expect(readFileSync(join(fixture, '_site/media/nodepilot-product-tour.mp4'), 'utf8'))
        .toBe('TOUR_VIDEO_SENTINEL')
      expect(readFileSync(join(fixture, '_site/demo/index.html'), 'utf8')).toBe('DEMO')
    } finally {
      rmSync(workspace, { recursive: true, force: true })
    }
  })

  it('excludes marketing binaries from git archive while retaining documentation source', () => {
    const fixture = mkdtempSync(join(tmpdir(), 'np-media-archive-'))
    if (!resolve(fixture).startsWith(join(resolve(tmpdir()), 'np-media-archive-')))
      throw new Error('Unexpected fixture path')
    try {
      const media = join(fixture, 'src/nodepilot-docs-ui/pages-media')
      mkdirSync(media, { recursive: true })
      writeFileSync(join(media, 'tour.mp4'), 'MARKETING_BINARY_SENTINEL')
      writeFileSync(join(fixture, 'src/nodepilot-docs-ui/source.ts'), 'DOCUMENTATION_SOURCE_SENTINEL')
      copyFileSync(join(repoRoot, '.gitattributes'), join(fixture, '.gitattributes'))
      const git = (...args) => execFileSync('git', args, { cwd: fixture, windowsHide: true })
      git('init', '--quiet')
      git('add', '.')
      const tree = git('write-tree').toString().trim()
      const archive = git('archive', '--format=tar', tree)
      expect(archive.includes(Buffer.from('MARKETING_BINARY_SENTINEL'))).toBe(false)
      expect(archive.includes(Buffer.from('DOCUMENTATION_SOURCE_SENTINEL'))).toBe(true)
    } finally {
      rmSync(fixture, { recursive: true, force: true })
    }
  })
})
