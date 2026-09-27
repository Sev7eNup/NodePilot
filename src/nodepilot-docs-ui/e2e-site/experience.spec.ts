import { expect, test } from '@playwright/test'

test('offers ten guided tasks with the current language and no second workflow renderer', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/walkthrough/')
  await expect(page.locator('.experience-mission').first()).toHaveAttribute('data-mission', 'build')
  await expect(page.locator('.experience-mission').nth(1)).toHaveAttribute('data-mission', 'diagnose')
  await expect(page.locator('.experience-mission').nth(2)).toHaveAttribute('data-mission', 'file')
  await expect(page.locator('.experience-mission-number').first()).toHaveText('01')
  await expect(page.locator('.experience-mission-number').nth(2)).toHaveText('03')
  await expect(page.locator('.experience-more')).toHaveCount(0)
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', '/demo/?tour=file&lang=de')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', '/demo/?tour=diagnose&lang=de')
  for (const id of ['build', 'decision', 'parallel', 'service', 'live', 'versions', 'machine', 'maintenance']) {
    await expect(page.locator(`[data-tour="${id}"]`)).toHaveAttribute('href', `/demo/?tour=${id}&lang=de`)
  }
  await expect(page.locator('#experience-root canvas')).toHaveCount(0)
  await page.locator('.header-lang [data-lang="en"]').click()
  await expect(page.locator('[data-tour="file"]')).toHaveText('Start guided walkthrough')
  await expect(page.locator('[data-tour="file"]')).toHaveAttribute('href', '/demo/?tour=file&lang=en')
  await expect(page.locator('[data-tour="diagnose"]')).toHaveAttribute('href', '/demo/?tour=diagnose&lang=en')
  for (const id of ['build', 'decision', 'parallel', 'service', 'live', 'versions', 'machine', 'maintenance']) {
    await expect(page.locator(`[data-tour="${id}"]`)).toHaveAttribute('href', `/demo/?tour=${id}&lang=en`)
  }
  await page.locator('[data-nav="home"]').click()
  await expect(page.locator('#experience-root')).toHaveCount(0)
  await page.locator('[data-nav="experience"]').click()
  await expect(page.locator('.experience-mission')).toHaveCount(10)
  const backgrounds = await page.locator('.experience-mission').evaluateAll(cards => cards.map(card => ({
    color: getComputedStyle(card).backgroundColor,
    image: getComputedStyle(card).backgroundImage,
  })))
  expect(new Set(backgrounds.map(background => background.color)).size).toBe(1)
  expect(new Set(backgrounds.map(background => background.image)).size).toBe(10)
  expect(errors).toEqual([])
})

for (const width of [390, 768, 1440]) {
  test(`guided entry fits ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.goto('/walkthrough/')
    await expect(page.locator('[data-tour="file"]')).toBeVisible()
    await expect(page.locator('[data-tour="maintenance"]')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    await page.screenshot({ path: test.info().outputPath(`entry-${width}.png`), fullPage: true })
  })
}

// A relative link resolves against the address, so every page writes its links against the site
// root. Otherwise a click from /walkthrough/ would stack another segment onto the path.
test('keeps every link pointing at the same place after navigating between pages', async ({ page }) => {
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
