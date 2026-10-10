import { expect, test } from '@playwright/test'

const DOCS = 'http://127.0.0.1:5187'
const STORAGE_KEY = 'nodepilot-docs-lang'

for (const [locale, lang] of [['de-AT', 'de'], ['de-CH', 'de'], ['en-US', 'en'], ['fr-FR', 'en']] as const) {
  test.describe(locale, () => {
    test.use({ locale })

    test('uses the browser default in website and docs without storing a choice', async ({ page }) => {
      await page.goto('/')
      await expect(page.locator('html')).toHaveAttribute('lang', lang)
      await expect(page.locator('[data-nav="product"]')).toHaveAttribute('href', lang === 'en' ? '/en/product/' : '/product/')
      expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBeNull()

      await page.goto(`${DOCS}/?source=language#example`)
      await expect(page).toHaveURL(`${DOCS}/${lang}/getting-started/introduction/?source=language#example`)
      await expect(page.locator('html')).toHaveAttribute('lang', lang)
      expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBeNull()
    })
  })
}

test.describe('explicit language choices', () => {
  test.use({ locale: 'en-US' })

  test('remembers the website switch across reloads and new entries, preserving the page and URL suffix', async ({ page }) => {
    await page.goto('/product/?source=language#example')
    await expect(page).toHaveURL(/\/en\/product\/\?source=language#example$/)
    await page.locator('.header-lang [data-lang="de"]').click()
    await expect(page).toHaveURL(/\/product\/\?source=language#example$/)
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBe('de')
    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')

    // A translated link displays its own language without replacing the saved preference.
    await page.goto('/en/product/')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBe('de')
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')
  })

  test('remembers only explicit docs choices, including confirmation of the active language', async ({ page }) => {
    await page.goto(`${DOCS}/de/cli/?source=language#example`)
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBeNull()
    await page.getByRole('button', { name: 'Deutsch', exact: true }).click()
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBe('de')
    await page.goto(`${DOCS}/`)
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')

    await page.goto(`${DOCS}/de/cli/?source=language#example`)
    await page.getByRole('button', { name: 'English', exact: true }).click()
    await expect(page).toHaveURL(`${DOCS}/en/cli/?source=language#example`)
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBe('en')
    await page.goBack()
    await expect(page.locator('html')).toHaveAttribute('lang', 'de')
    expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBe('en')
    await page.goto(`${DOCS}/`)
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  })

  test('keeps German-only legal pages accessible with an English browser', async ({ page }) => {
    for (const path of ['/impressum/', '/datenschutz/']) {
      await page.goto(path)
      await expect(page.locator('html')).toHaveAttribute('lang', 'de')
      await expect(page.locator('h1')).toBeVisible()
      expect(new URL(page.url()).pathname).toBe(path)
      expect(await page.evaluate(key => localStorage.getItem(key), STORAGE_KEY)).toBeNull()
    }
  })
})
