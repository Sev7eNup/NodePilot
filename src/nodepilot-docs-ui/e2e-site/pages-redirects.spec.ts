import { expect, test } from '@playwright/test'
import { existsSync, readFileSync, statSync } from 'node:fs'
import { join, resolve } from 'node:path'

const source = 'https://sev7enup.github.io/NodePilot'
const destination = 'https://www.nodepilot.run'
const artifact = resolve('_pages-redirects')

test.beforeEach(async ({ page }) => {
  // Serve the actual Pages artifact, including its 404 body, without hitting either live host.
  await page.route(`${source}/**`, async route => {
    const pathname = new URL(route.request().url()).pathname.slice('/NodePilot/'.length)
    let file = join(artifact, pathname)
    if (existsSync(file) && statSync(file).isDirectory()) file = join(file, 'index.html')
    const missing = !existsSync(file)
    await route.fulfill({
      status: missing ? 404 : 200,
      contentType: file.endsWith('.js') ? 'text/javascript' : 'text/html',
      body: readFileSync(missing ? join(artifact, '404.html') : file),
    })
  })
  await page.route(`${destination}/**`, route => route.fulfill({ contentType: 'text/html', body: '<h1>Destination</h1>' }))
})

for (const [old, target] of [
  ['/', '/'],
  ['/docs/de/cli/?ref=reddit#options', '/docs/de/cli/?ref=reddit#options'],
  ['/#/de/cli', '/docs/de/cli/'],
  ['/docs/#/security/hardening', '/docs/en/security/hardening/'],
  ['/#/produkt', '/product/'],
  ['/demo/?lang=de&tour=file#/workflows/abc', '/demo/workflows/abc?lang=de&tour=file'],
  ['/demo/workflows/abc?lang=en', '/demo/workflows/abc?lang=en'],
  ['/unknown/path?ref=reddit#section', '/unknown/path?ref=reddit#section'],
  ['/demo/#//evil.example', '/'],
]) {
  test(`old Pages link ${old} reaches its matching canonical URL`, async ({ page }) => {
    await page.goto(source + old)
    await expect(page).toHaveURL(destination + target)
    await expect(page.getByRole('heading')).toHaveText('Destination')
  })
}

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false })
  test('a known documentation page still forwards through meta refresh', async ({ page }) => {
    await page.goto(`${source}/docs/de/cli/`)
    await expect(page).toHaveURL(`${destination}/docs/de/cli/`)
  })
})
