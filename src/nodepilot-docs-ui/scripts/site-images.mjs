import { fileURLToPath } from 'node:url'
import { mkdirSync, writeFileSync } from 'node:fs'
import sharp from 'sharp'

const sources = {
  designer: '../../../docs/images/designer-dark.png',
  logs: '../../../docs/images/log-dark.png',
  ai: '../../../docs/images/ai-dark.png',
  dashboard: '../../../docs/images/dashboard-dark.png',
  liveops: '../../../docs/images/liveops-dark.png',
  'first-workflow-designer': '../content/blog/images/first-workflow-designer.png',
  'first-workflow-run': '../content/blog/images/first-workflow-run.png',
}

export async function buildSiteImages() {
  const output = new URL('../src/site/public/site-images/', import.meta.url)
  mkdirSync(output, { recursive: true })
  const manifest = {}
  for (const [key, source] of Object.entries(sources)) {
    const image = sharp(fileURLToPath(new URL(source, import.meta.url)))
    const metadata = await image.metadata()
    const widths = [...new Set([480, 960, 1600, metadata.width].filter(width => width <= metadata.width))]
    for (const width of widths) await image.clone().resize({ width }).webp({ quality: 82 }).toFile(fileURLToPath(new URL(`${key}-${width}.webp`, output)))
    manifest[key] = { width: metadata.width, height: metadata.height, widths }
  }
  writeFileSync(new URL('../src/site/image-manifest.json', import.meta.url), JSON.stringify(manifest, null, 2) + '\n')
}

await buildSiteImages()
