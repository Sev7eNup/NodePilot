import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const packageRoot = fileURLToPath(new URL('../../', import.meta.url))
const repoRoot = resolve(packageRoot, '../..')

describe('Pages-only media packaging', () => {
  it('keeps the tour available to Pages outside the installer public directory', () => {
    for (const name of ['nodepilot-product-tour.mp4', 'product-tour-poster.png']) {
      expect(existsSync(join(packageRoot, 'pages-media', name))).toBe(true)
      expect(existsSync(join(packageRoot, 'public/media', name))).toBe(false)
    }
    expect(readFileSync(join(repoRoot, '.github/workflows/docs-pages.yml'), 'utf8'))
      .toContain('cp -R pages-media dist/media')
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
