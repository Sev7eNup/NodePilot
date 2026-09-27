import { ARTICLE_SLUGS } from '../src/site/router'
import { expect, test } from '@playwright/test'
import { fileURLToPath } from 'node:url'

const siteRoutes = ['', 'product/', 'walkthrough/', 'tutorials/', 'blog/', ...ARTICLE_SLUGS.map(slug => `blog/${slug}/`), 'powershell-automation/', 'scorch-alternative/', 'self-hosted-automation/']

test.describe('static SEO content', () => {
  test.use({ javaScriptEnabled: false })
  test('home title and Media page are crawlable in both languages', async ({ page, request, baseURL }) => {
    for (const language of ['de', 'en'] as const) {
      const prefix = language === 'en' ? '/en' : ''
      await page.goto(`${prefix}/`)
      await expect(page).toHaveTitle(/^NodePilot – .*Windows.*PowerShell/)
      await expect(page.locator('meta[property="og:title"]')).toHaveAttribute('content', await page.title())

      await page.goto(`${prefix}/tutorials/`)
      await expect(page.locator('[data-nav="videos"]')).toHaveText('Media')
      await expect(page.locator('#videos-page h1')).toContainText('Media')
      await expect(page).toHaveTitle(/^Media: .*?(?:Videos|videos)/)
      await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', `${baseURL}${prefix}/tutorials/`)
      await expect(page.locator('meta[name="description"]')).toHaveAttribute('content', /(?:Video-Tutorials|video tutorials)/)
      await page.setViewportSize({ width: 390, height: 844 })
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    }
    const sitemap = await (await request.get('/sitemap.xml')).text()
    expect(sitemap).toContain(`<loc>${baseURL}/tutorials/</loc>`)
    expect(sitemap).toContain(`<loc>${baseURL}/en/tutorials/</loc>`)
  })
  for (const language of ['de', 'en']) {
    test(`${language}: every page contains its content, self canonical and reciprocal languages without JS`, async ({ page, request, baseURL }) => {
      for (const route of siteRoutes) {
        const path = `/${language === 'en' ? 'en/' : ''}${route}`
        const response = await page.goto(path)
        expect(response?.status(), path).toBe(200)
        await expect(page.locator('html')).toHaveAttribute('lang', language)
        // Not just one visible h1: the file carries no other page's section at all.
        await expect(page.locator('h1')).toHaveCount(1)
        await expect(page.locator('h1')).not.toBeEmpty()
        await expect(page.locator('main > .page[id]')).toHaveCount(1)
        await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', `${baseURL}${path}`)
        await expect(page.locator('link[hreflang="en"]')).toHaveAttribute('href', `${baseURL}/en/${route}`)
        await expect(page.locator('link[hreflang="de"]')).toHaveAttribute('href', `${baseURL}/${route}`)
        const schemas = await page.locator('script[data-site-schema]').textContent()
        expect(JSON.parse(schemas!)['@graph']).toBeDefined()
        if (route.startsWith('blog/') && route !== 'blog/') {
          await expect(page.locator('#article-body')).toBeVisible()
          expect((await page.locator('#article-body').innerText()).length).toBeGreaterThan(800)
        }
        if (route === 'tutorials/') {
          // Without script a card is a plain link to the file of this language.
          await expect(page.locator('a.video-card')).toHaveCount(25)
          await expect(page.locator('a.video-card').first()).toHaveAttribute('href', new RegExp(`^/media/training/00-intro-${language}\\.[0-9a-f]{10}\\.mp4$`))
        }
      }
      await page.goto(`/${language === 'en' ? 'en/' : ''}blog/first-workflow/`)
      await expect(page.locator('#article-body h2')).toHaveCount(4)
      await expect(page.locator('#article-body')).toContainText('Greeting Probe')
      for (const image of await page.locator('#article-body img').all()) {
        const src = await image.getAttribute('src')
        expect((await request.get(src!)).status()).toBe(200)
        await expect(image).toHaveAttribute('alt', /.+/)
        await expect(image).toHaveAttribute('width', /\d+/)
      }
    })
  }
  test('documentation ships the full chapter, anchors and working sibling links', async ({ page }) => {
    await page.goto('http://127.0.0.1:5187/de/getting-started/quickstart/')
    await expect(page.locator('article h1')).toHaveText('Schnelleinstieg')
    await expect(page.locator('article')).toContainText('hostInfo')
    expect(await page.locator('article h2[id]').count()).toBeGreaterThan(3)
    const install = page.locator('article a[href$="getting-started/installation/"]').first()
    await install.click()
    await expect(page.locator('article h1')).toHaveText('Installation')
  })
})

test('language URLs, navigation metadata, history and article links stay consistent', async ({ page, baseURL }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/blog/first-workflow/?source=test#3-powershell-skript-hinterlegen')
  await page.locator('.header-lang [data-lang="en"]').click()
  await expect(page).toHaveURL(/\/en\/blog\/first-workflow\/\?source=test#/)
  await expect(page.locator('#article-title')).toHaveText('Creating a first workflow with NodePilot')
  await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', `${baseURL}/en/blog/first-workflow/`)
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  await page.locator('#article-page [data-site-path="powershell-automation"]').first().click()
  await expect(page.locator('#solution-title')).toContainText('PowerShell')
  await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', `${baseURL}/en/powershell-automation/`)
  await page.goBack()
  await expect(page.locator('#article-title')).toHaveText('Creating a first workflow with NodePilot')
  await page.goForward()
  await expect(page.locator('#solution-page')).toBeVisible()
  await page.locator('[data-nav="blog"]').click()
  await expect(page.locator('.blog-index-row:visible')).toHaveCount(16)
  await page.locator('#blog-search').fill('first workflow')
  await expect(page.locator('.blog-index-row:visible')).toHaveCount(1)
  expect(errors).toEqual([])
})

for (const width of [390, 1440]) {
  test(`new pages and third article fit ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    for (const path of ['/powershell-automation/', '/en/scorch-alternative/', '/self-hosted-automation/', ...ARTICLE_SLUGS.flatMap(slug => [`/blog/${slug}/`, `/en/blog/${slug}/`]), '/']) {
      await page.goto(path)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), path).toBe(true)
    }
  })
}

test('article navigation loads only the selected body and keeps body text out of shared JavaScript', async ({ page, request }) => {
  await page.goto('/blog/first-workflow/')
  const paragraph = await page.locator('#article-body p').first().innerText()
  const script = await page.locator('script[type="module"][src]').getAttribute('src')
  const bundle = await (await request.get(script!)).text()
  expect(bundle).not.toContain(paragraph.slice(0, 100))
  await page.locator('#article-page [data-site-path="blog/verify-results"]').click()
  await expect(page).toHaveURL(/\/blog\/verify-results\/$/)
  await expect(page.locator('#article-body')).not.toContainText('Greeting Probe')
  await page.goBack()
  await expect(page.locator('#article-body')).toContainText('Greeting Probe')
  await page.reload()
  await expect(page.locator('#article-body')).toContainText('hostInfo')
  await page.goto('http://127.0.0.1:5187/de/getting-started/quickstart/')
  const docsScript = await page.locator('script[type="module"][src]').getAttribute('src')
  const docsBundle = await (await request.get(new URL(docsScript!, page.url()).href)).text()
  expect(docsBundle).not.toContain('The migration example below is fictional.')
  expect(docsBundle).not.toContain('Das folgende Migrationsbeispiel ist konstruiert.')
})

test('mobile product screenshot selects a small responsive image and switches tabs', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 })
  await page.goto('/product/')
  const image = page.locator('#product-image')
  await image.scrollIntoViewIfNeeded()
  await expect(image).toHaveAttribute('srcset', /480w, .*960w/)
  await expect.poll(() => image.evaluate(img => (img as HTMLImageElement).currentSrc)).toMatch(/designer-480\.webp$/)
  await page.locator('[data-product-tab="logs"]').click()
  await expect(image).toHaveAttribute('data-media', 'logs')
  await expect(image).toHaveAttribute('srcset', /logs-480\.webp/)
})

test.describe('training videos', () => {
  const sample = fileURLToPath(new URL('./fixtures/training-sample.webm', import.meta.url))

  test('a card plays its video large in the dialog, and closing stops it and returns focus', async ({ page }) => {
    // WebM stands in for the MP4s: the bundled Chromium plays no H.264.
    await page.route('**/media/training/*.mp4', route => route.fulfill({ path: sample, contentType: 'video/webm' }))
    await page.goto('/en/tutorials/')
    const card = page.locator('a.video-card').nth(4)
    await card.click()
    const dialog = page.locator('#video-dialog')
    await expect(dialog).toBeVisible()
    await expect(page.locator('#video-dialog-name')).toHaveText(await card.locator('.video-title').innerText())
    expect((await dialog.boundingBox())!.width).toBeGreaterThan(1000)
    const video = page.locator('#video-player')
    await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.readyState)).toBeGreaterThanOrEqual(1)
    await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.currentTime)).toBeGreaterThan(0.2)
    await expect(page.locator('#video-youtube-link')).toBeHidden()

    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
    // The close event is dispatched after the dialog hides.
    await expect.poll(() => video.evaluate((element: HTMLVideoElement) => [element.paused, element.getAttribute('src')])).toEqual([true, null])
    await expect(card).toBeFocused()
  })

  test('a video that does not load offers the file instead', async ({ page }) => {
    await page.route('**/media/training/*.mp4', route => route.fulfill({ status: 404, body: '' }))
    await page.goto('/tutorials/')
    const card = page.locator('a.video-card').first()
    await card.click()
    await expect(page.locator('#video-error')).toBeVisible()
    await expect(page.locator('#video-error-link')).toHaveAttribute('href', new RegExp(`${await card.getAttribute('href')}$`))
  })
})
