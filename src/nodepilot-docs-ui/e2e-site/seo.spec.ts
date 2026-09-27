import { ARTICLE_SLUGS } from '../src/site/router'
import { expect, test } from '@playwright/test'

const siteRoutes = ['', 'product/', 'walkthrough/', 'blog/', ...ARTICLE_SLUGS.map(slug => `blog/${slug}/`), 'powershell-automation/', 'scorch-alternative/', 'self-hosted-automation/']

test.describe('static SEO content', () => {
  test.use({ javaScriptEnabled: false })
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
