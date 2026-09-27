// Publishes the training series "NodePilot in 2 minutes" to the website.
//
//   ../../out/nodepilot-training/<NN-slug>-<lang>.mp4   rendered episodes (gitignored)
//   -> pages-media/training/<NN-slug>-<lang>.<hash>.mp4  copied for assemble-site (gitignored)
//   -> src/site/public/training/<NN-slug>-<lang>.<hash>.webp  poster from an app capture (committed)
//   -> src/site/videos.json                               catalog the page renders from (committed)
//
// The content hash in each file name gives a changed video a new URL, so neither caches nor the
// deploy's presence check can serve an old version. Run it after rendering episodes; it keeps
// publishedAt and youtube from the existing catalog. The episode sources and renders are gitignored,
// so from a second worktree point NP_TRAINING_ROOT at the checkout that holds them.
import { createHash } from 'node:crypto'
import { closeSync, copyFileSync, existsSync, mkdirSync, openSync, readFileSync, readSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const LANGS = ['de', 'en']
const at = (path) => fileURLToPath(new URL(path, import.meta.url))
const SOURCE_ROOT = process.env.NP_TRAINING_ROOT ? `${process.env.NP_TRAINING_ROOT.replace(/[\\/]+$/, '')}/` : at('../../../')
const EPISODES = `${SOURCE_ROOT}scripts/marketing-film/training/episodes/`
const CAPTURES = `${SOURCE_ROOT}scripts/marketing-film/training/captures/`
const RENDERS = `${SOURCE_ROOT}out/nodepilot-training/`

// The app screen each poster shows and the part of it that fills the 16:9 card, as fractions of
// the capture: [left, top, width]. The height follows from the aspect ratio. A whole window at card
// size is too small to read, so each poster zooms in on what the episode is about.
const POSTER_CAPTURES = {
  '00-intro': ['designer', 0.14, 0.15, 0.6], '01-dashboard': ['dash', 0.18, 0.33, 0.62],
  '02-first-workflow': ['connected', 0.38, 0.15, 0.6], '03-edit-publish': ['diff', 0.15, 0.08, 0.7],
  '04-run-live': ['running', 0.33, 0.1, 0.55], '05-machines': ['machines', 0.2, 0.08, 0.6],
  '06-data': ['pillSet', 0.4, 0.15, 0.6], '07-decisions': ['graph', 0.12, 0.12, 0.8],
  '08-activities': ['httpPanel', 0.25, 0.12, 0.6], '09-triggers': ['cron', 0.4, 0.15, 0.6],
  '10-failures': ['failedStep', 0.02, 0.5, 0.5], '11-debugger': ['paused', 0.38, 0.05, 0.62],
  '12-team-alerts': ['routesFilled', 0.14, 0.08, 0.6], '13-import-export': ['scorch', 0.2, 0.12, 0.6],
  '14-ai': ['proposal', 0.6, 0.1, 0.4], '15-subworkflows': ['swContract', 0.28, 0.12, 0.7],
  '16-custom-nodes': ['palette', 0, 0.3, 0.55], '17-metrics-log': ['charts', 0.2, 0.06, 0.8],
  '18-trigger-api': ['execs', 0.02, 0.08, 0.6], '19-users': ['rootPerms', 0.2, 0.18, 0.55],
  '20-ldap-sso': ['ldap', 0.2, 0.25, 0.6], '21-audit': ['audit', 0.2, 0.15, 0.65],
  '22-backup': ['preview', 0.15, 0.08, 0.5], '23-cli': ['expanded', 0.1, 0.2, 0.65],
  '24-mcp': ['run', 0, 0.2, 0.55],
}
const MEDIA = at('../pages-media/training/')
const POSTERS = at('../src/site/public/training/')
const CATALOG = at('../src/site/videos.json')

/** Number, slug and titles from an episode module, read as text: importing it would load the canvas kit. */
export function parseEpisode(source) {
  const number = source.match(/\bnumber:\s*(\d+)/)?.[1]
  const slug = source.match(/\bslug:\s*'([^']+)'/)?.[1]
  const title = source.match(/\btitle:\s*\{\s*de:\s*(['"])(.*?)\1,\s*en:\s*(['"])(.*?)\3\s*\}/)
  if (number === undefined || !slug || !title) throw new Error('Episode without number, slug or title.')
  return { number: Number(number), slug, title: { de: title[2], en: title[4] } }
}

/** Duration in whole seconds, from the mvhd box inside moov. */
export function mp4Duration(file) {
  const fd = openSync(file, 'r')
  try {
    const size = statSync(file).size
    const read = (offset, length) => {
      const buffer = Buffer.alloc(length)
      readSync(fd, buffer, 0, length, offset)
      return buffer
    }
    // Walks sibling boxes from `start` to `end` and returns the payload offset of `type`.
    const find = (type, start, end) => {
      for (let offset = start; offset + 8 <= end;) {
        const header = read(offset, 16)
        let length = header.readUInt32BE(0)
        let body = offset + 8
        if (length === 1) { length = Number(header.readBigUInt64BE(8)); body = offset + 16 }
        else if (length === 0) length = end - offset
        if (header.toString('latin1', 4, 8) === type) return { body, end: offset + length }
        offset += length
      }
      throw new Error(`${file}: no ${type} box.`)
    }
    const moov = find('moov', 0, size)
    const mvhd = find('mvhd', moov.body, moov.end)
    const box = read(mvhd.body, 32)
    const [timescale, duration] = box[0] === 1
      ? [box.readUInt32BE(20), Number(box.readBigUInt64BE(24))]
      : [box.readUInt32BE(12), box.readUInt32BE(16)]
    return Math.round(duration / timescale)
  } finally {
    closeSync(fd)
  }
}

const hash = (data) => createHash('sha256').update(data).digest('hex').slice(0, 10)

/** Deletes every file of `dir` named `<stem>.<hash>.<ext>` except `keep`. */
function removeStale(dir, stem, ext, keep) {
  for (const name of readdirSync(dir)) {
    if (name !== keep && name.startsWith(`${stem}.`) && name.endsWith(`.${ext}`)) rmSync(`${dir}${name}`)
  }
}

export async function buildSiteVideos() {
  mkdirSync(MEDIA, { recursive: true })
  mkdirSync(POSTERS, { recursive: true })
  const previous = existsSync(CATALOG) ? JSON.parse(readFileSync(CATALOG, 'utf8')) : { episodes: [] }
  const today = new Date().toISOString().slice(0, 10)
  const episodes = []
  for (const name of readdirSync(EPISODES).filter((file) => /^\d\d-[a-z0-9-]+\.js$/.test(file)).sort()) {
    const { number, slug, title } = parseEpisode(readFileSync(`${EPISODES}${name}`, 'utf8'))
    const known = previous.episodes.find((episode) => episode.slug === slug)
    const text = {}
    for (const lang of LANGS) {
      const stem = `${slug}-${lang}`
      const source = `${RENDERS}${stem}.mp4`
      if (!existsSync(source)) throw new Error(`Missing render ${source}.`)
      const video = `${stem}.${hash(readFileSync(source))}.mp4`
      if (!existsSync(`${MEDIA}${video}`)) copyFileSync(source, `${MEDIA}${video}`)
      removeStale(MEDIA, stem, 'mp4', video)

      const [name, left, top, width] = POSTER_CAPTURES[slug]
      const capture = `${CAPTURES}${slug}/${lang}/${name}.png`
      if (!existsSync(capture)) throw new Error(`Missing poster capture ${capture}.`)
      const { width: w, height: h } = await sharp(capture).metadata()
      const region = { left: Math.round(left * w), top: Math.round(top * h), width: Math.round(width * w), height: Math.round(width * h) }
      region.width = Math.min(region.width, w - region.left)
      region.height = Math.min(region.height, h - region.top)
      const webp = await sharp(capture).extract(region).resize({ width: 640, height: 360, fit: 'cover' }).webp({ quality: 82 }).toBuffer()
      const poster = `${stem}.${hash(webp)}.webp`
      writeFileSync(`${POSTERS}${poster}`, webp)
      removeStale(POSTERS, stem, 'webp', poster)

      const youtube = known?.text?.[lang]?.youtube
      text[lang] = {
        title: title[lang],
        video: `media/training/${video}`,
        poster: `training/${poster}`,
        bytes: statSync(source).size,
        duration: mp4Duration(source),
        ...(youtube ? { youtube } : {}),
      }
    }
    episodes.push({ number, slug, publishedAt: known?.publishedAt ?? today, text })
  }
  writeFileSync(CATALOG, JSON.stringify({ episodes }, null, 2) + '\n')
  return episodes.length
}

if (import.meta.main) {
  try {
    console.log(`site-videos: ${await buildSiteVideos()} episode(s) written to src/site/videos.json`)
  } catch (error) {
    console.error(`site-videos: ${error.message}`)
    process.exitCode = 1
  }
}
