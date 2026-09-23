// Assembles the GitHub Pages tree from the separate build outputs:
//
//   dist-site/*              -> _site/              project website (npm run build:site)
//   dist/*                   -> _site/docs/         docs SPA, the bundle the installers ship (npm run build)
//                                                   the copy is stamped with np-site-root; see below
//   ../nodepilot-ui/dist-demo/* -> _site/demo/      browser demo of the app (npm run build:demo there)
//   pages-media/*            -> _site/media/        tour video and poster, kept out of the installers
//   public/og-image.png      -> _site/og-image.png  social preview image for the website
//
// The Pages workflow and `npm run preview:site` both run this script, so the layout exists once.
import { cpSync, existsSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { isAbsolute, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const DEMO_DIR = '../nodepilot-ui/dist-demo'

// Every input is required. An optional one would let a deploy publish a site that silently
// keeps the previous demo, which is the kind of failure nothing goes red for.
const REQUIRED_INPUTS = [
  ['dist/index.html', 'Run "npm run build" first.'],
  ['dist-site/index.html', 'Run "npm run build:site" first.'],
  [`${DEMO_DIR}/index.html`, 'Run "npm run build:demo" in src/nodepilot-ui first.'],
  ['pages-media', 'Source archives leave it out; assemble from a git checkout.'],
  ['public/og-image.png', ''],
]

// Top-level names the other inputs own in the output.
const RESERVED_NAMES = ['docs', 'demo', 'media', 'og-image.png']

/**
 * Marks the docs copy as the one published beside a website and a demo.
 *
 * The same `dist/` is also shipped inside the product under `wwwroot/docs`, where neither
 * neighbour exists. This script is the only code that knows the difference, so it stamps the
 * copy rather than the build — the product's docs stay untouched and render no back-links.
 * A `<meta>`, not a script: the docs page runs under `script-src 'self'`.
 */
const SITE_ROOT_META = '<meta name="np-site-root" content="../">'

/**
 * Every page of the docs, not only its entry: each address became its own file when the docs
 * got real addresses, and a reader landing deep in the tree needs the same back-links.
 */
function markDocsAsPagesCopy(docsDir) {
  let stamped = 0
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry)
      if (statSync(full).isDirectory()) {
        walk(full)
        continue
      }
      if (!entry.endsWith('.html')) continue
      const html = readFileSync(full, 'utf8')
      if (!html.includes('</head>')) throw new Error(`${full} has no </head> to mark as the Pages copy.`)
      writeFileSync(full, html.replace('</head>', `  ${SITE_ROOT_META}\n  </head>`))
      stamped++
    }
  }
  walk(docsDir)
  if (stamped === 0) throw new Error(`No HTML file under ${docsDir} to mark as the Pages copy.`)
  return stamped
}

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
  const inputDirs = ['dist', 'dist-site', DEMO_DIR, 'pages-media', 'public'].map(input)
  if (!isWithin(root, out) || inputDirs.some((dir) => isWithin(dir, out) || isWithin(out, dir)))
    throw new Error(`Refusing to replace ${out}: use a separate folder inside ${root}.`)

  rmSync(out, { recursive: true, force: true })
  cpSync(input('dist-site'), out, { recursive: true })
  cpSync(input('dist'), join(out, 'docs'), { recursive: true })
  markDocsAsPagesCopy(join(out, 'docs'))
  cpSync(input(DEMO_DIR), join(out, 'demo'), { recursive: true })
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
