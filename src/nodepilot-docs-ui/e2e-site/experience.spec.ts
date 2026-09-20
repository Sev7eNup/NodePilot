import { expect, test } from '@playwright/test'

test('offers both guided tasks with the current language and no second workflow renderer', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/#/erleben')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', 'demo/?tour=file&lang=de')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', 'demo/?tour=diagnose&lang=de')
  await expect(page.locator('#experience-root canvas')).toHaveCount(0)
  await page.locator('.header-lang [data-lang="en"]').click()
  await expect(page.locator('[data-tour="file"]')).toHaveText('Start guided walkthrough')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', 'demo/?tour=file&lang=en')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', 'demo/?tour=diagnose&lang=en')
  await page.locator('[data-nav="home"]').click()
  await expect(page.locator('#experience-root')).toBeEmpty()
  await page.locator('[data-nav="experience"]').click()
  await expect(page.locator('.experience-mission')).toHaveCount(2)
  expect(errors).toEqual([])
})

for (const width of [390, 768, 1440]) {
  test(`guided entry fits ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.goto('/#/erleben')
    await expect(page.locator('[data-tour="file"]')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    await page.screenshot({ path: test.info().outputPath(`entry-${width}.png`), fullPage: true })
  })
}
