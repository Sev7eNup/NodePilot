// Assembles the GitHub Pages tree from the separate build outputs:
//
//   dist-site/*          -> _site/              project website (npm run build:site)
//   dist/*               -> _site/docs/         docs SPA, the bundle the installers ship (npm run build)
//   pages-media/*        -> _site/media/        tour video and poster, kept out of the installers
//   public/og-image.png  -> _site/og-image.png  social preview image for the website
//
// The Pages workflow and `npm run preview:site` both run this script, so the layout exists once.
import { cpSync, existsSync, rmSync } from 'node:fs'
import { isAbsolute, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const REQUIRED_INPUTS = [
  ['dist/index.html', 'Run "npm run build" first.'],
  ['dist-site/index.html', 'Run "npm run build:site" first.'],
  ['pages-media', 'Source archives leave it out; assemble from a git checkout.'],
  ['public/og-image.png', ''],
]

// Top-level names the other inputs own in the output.
const RESERVED_NAMES = ['docs', 'media', 'og-image.png']

/** True when `path` is `dir` or lies inside it. */
function isWithin(dir, path) {
  const rel = relative(dir, path)
  return rel === '' || (!isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${sep}`))
}

/**
 * Writes the Pages tree and returns its absolute path. `outDir` resolves against `packageRoot`.
 * It is deleted first, so it has to be a folder inside the package that no input shares.
 */
export function assembleSite(packageRoot, outDir = '_site') {
  const root = resolve(packageRoot)
  const out = resolve(root, outDir)
  const input = (path) => join(root, path)

  for (const [path, hint] of REQUIRED_INPUTS) {
    if (!existsSync(input(path))) throw new Error(`Missing ${path}. ${hint}`.trim())
  }
  for (const name of RESERVED_NAMES) {
    if (existsSync(input(join('dist-site', name))))
      throw new Error(`dist-site/${name} collides with the Pages layout, which uses that name.`)
  }
  const inputDirs = ['dist', 'dist-site', 'pages-media', 'public'].map(input)
  if (!isWithin(root, out) || inputDirs.some((dir) => isWithin(dir, out) || isWithin(out, dir)))
    throw new Error(`Refusing to replace ${out}: use a separate folder inside ${root}.`)

  rmSync(out, { recursive: true, force: true })
  cpSync(input('dist-site'), out, { recursive: true })
  cpSync(input('dist'), join(out, 'docs'), { recursive: true })
  cpSync(input('pages-media'), join(out, 'media'), { recursive: true })
  cpSync(input('public/og-image.png'), join(out, 'og-image.png'))
  return out
}

if (import.meta.main) {
  try {
    const out = assembleSite(fileURLToPath(new URL('..', import.meta.url)))
    console.log(`assemble-site: wrote ${out}`)
  } catch (error) {
    console.error(`assemble-site: ${error.message}`)
    process.exitCode = 1
  }
}
