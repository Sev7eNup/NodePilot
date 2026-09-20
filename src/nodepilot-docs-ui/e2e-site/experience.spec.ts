import { expect, test } from '@playwright/test'

test('offers both guided tasks with the current language and no second workflow renderer', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/walkthrough/')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', '/demo/?tour=file&lang=de')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', '/demo/?tour=diagnose&lang=de')
  await expect(page.locator('#experience-root canvas')).toHaveCount(0)
  await page.locator('.header-lang [data-lang="en"]').click()
  await expect(page.locator('[data-tour="file"]')).toHaveText('Start guided walkthrough')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', '/demo/?tour=file&lang=en')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', '/demo/?tour=diagnose&lang=en')
  await page.locator('[data-nav="home"]').click()
  await expect(page.locator('#experience-root')).toBeEmpty()
  await page.locator('[data-nav="experience"]').click()
  await expect(page.locator('.experience-mission')).toHaveCount(2)
  expect(errors).toEqual([])
})

for (const width of [390, 768, 1440]) {
  test(`guided entry fits ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.goto('/walkthrough/')
    await expect(page.locator('[data-tour="file"]')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    await page.screenshot({ path: test.info().outputPath(`entry-${width}.png`), fullPage: true })
  })
}

// A relative link resolves against the address, and History API navigation changes it. Reached
// from the home page, `href="product/"` would point at /walkthrough/product/ from there on, and
// every further click would stack another segment onto a page that never changes.
test('keeps every link pointing at the same place after navigating in the page', async ({ page }) => {
  await page.goto('/')
  await page.locator('[data-nav="experience"]').click()
  await expect(page).toHaveURL('/walkthrough/')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', '/demo/?tour=file&lang=de')
  for (const [nav, path] of [
    ['product', '/product/'],
    ['blog', '/blog/'],
    ['experience', '/walkthrough/'],
    ['home', '/'],
  ] as const) {
    await page.locator(`[data-nav="${nav}"]`).first().click()
    await expect(page).toHaveURL(path)
  }
})
