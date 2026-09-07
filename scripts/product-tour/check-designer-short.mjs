import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'src/nodepilot-ui/package.json'));
const { chromium } = require('playwright');
const out = path.join(root, process.argv.includes('--docs-images') ? 'out/nodepilot-designer-short-8s-v2' : 'out/nodepilot-designer-short-8s');
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1360, height: 1000 } });
const errors = [];
page.on('pageerror', e => errors.push(e.message));
try {
  await page.goto(pathToFileURL(path.join(out, 'Ansehen.html')).href);
  const metadata = await page.locator('video').evaluate(async v => {
    if (v.readyState < 1) await new Promise((ok, fail) => { v.onloadedmetadata = ok; v.onerror = fail; });
    const result = { duration: v.duration, width: v.videoWidth, height: v.videoHeight, error: v.error };
    v.muted = true;
    await v.play();
    await new Promise(ok => setTimeout(ok, 700));
    result.currentTimeAfterPlay = v.currentTime;
    result.decodedFrames = v.getVideoPlaybackQuality().totalVideoFrames;
    result.droppedFrames = v.getVideoPlaybackQuality().droppedVideoFrames;
    v.pause();
    return result;
  });
  if (metadata.duration !== 8 || metadata.width !== 1080 || metadata.height !== 1920 || metadata.decodedFrames < 5 || metadata.error) throw new Error('Invalid playback metadata ' + JSON.stringify(metadata));
  const seekChecks = [];
  for (const t of [.25, 1.05, 1.85, 2.85, 3.95, 5.5, 7.3, 7.95]) {
    const actual = await page.locator('video').evaluate(async (v, t) => {
      await new Promise(ok => { v.addEventListener('seeked', ok, { once: true }); v.currentTime = t; });
      await new Promise(ok => requestAnimationFrame(() => requestAnimationFrame(ok)));
      return { time: v.currentTime, readyState: v.readyState, error: v.error };
    }, t);
    if (actual.error || actual.readyState < 2 || Math.abs(actual.time-t) > .02) throw new Error('Seek failed');
    seekChecks.push(actual);
    await page.locator('video').screenshot({ path: path.join(out, 'raw', `playback-${t.toFixed(2)}.png`) });
  }
  await page.screenshot({ path: path.join(out, 'raw/preview-page.png') });
  const capture = JSON.parse(await readFile(path.join(out, 'assets/capture-check.json'), 'utf8'));
  if (errors.length || capture.errors.length || capture.requests.some(r => r.method !== 'GET')) throw new Error('Browser/API verification failed');
  const revisionChecks = {};
  if (process.argv.includes('--docs-images')) {
    const digest = async file => createHash('sha256').update(await readFile(file)).digest('hex');
    const previous = path.join(root, 'out/nodepilot-designer-short-8s');
    for (const relative of ['assets/designer-canvas.png', 'assets/designer-geometry.json', 'raw/original-designer-soundtrack.wav',
      ...['1.05', '1.85', '2.85', '3.95', '5.50'].map(t => `raw/preview-${t}.png`)]) {
      revisionChecks[relative] = (await digest(path.join(out, relative))) === (await digest(path.join(previous, relative)));
    }
    for (const name of ['reddit-avatar.png', 'og-image.png']) {
      revisionChecks[`unaltered-source/${name}`] = (await digest(path.join(out, 'assets', name))) === (await digest(path.join(root, 'docs/images', name)));
    }
    const exported = JSON.parse(await readFile(path.join(out, 'export.json'), 'utf8'));
    revisionChecks.noGeneratedImageInputs = exported.images.generatedImageInputs.length === 0;
    if (Object.values(revisionChecks).some(value => !value)) throw new Error('Revision preservation failed ' + JSON.stringify(revisionChecks));
  }
  const result = { metadata, seekChecks, browserErrors: errors, nativeCaptureErrors: capture.errors, mockedApiReadOnly: true, revisionChecks };
  await writeFile(path.join(out, 'playback-check.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally { await browser.close(); }
