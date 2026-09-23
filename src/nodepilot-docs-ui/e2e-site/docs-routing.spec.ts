import { expect, test } from '@playwright/test'

/**
 * The documentation's addresses are real files now, one per page and language. What can go
 * wrong is invisible on the page itself: a server that hands out the root shell for a deep
 * address renders a blank page and reports nothing, so every check below looks at the page's
 * own title and at whether a single request failed.
 */
const DOCS = 'http://127.0.0.1:5187'

test('serves a deep address as its own page, with every asset reachable from that depth', async ({ page }) => {
  const failed: string[] = []
  page.on('response', (response) => {
    if (response.status() >= 400) failed.push(`${response.status()} ${response.url()}`)
  })

  await page.goto(`${DOCS}/de/cli/`)

  // The shell's title would mean the server fell back to the documentation root.
  await expect(page).toHaveTitle('CLI (np) — NodePilot Dokumentation')
  await expect(page.locator('h1')).toHaveText('CLI (np)')
  await expect(page.locator('html')).toHaveAttribute('lang', 'de')
  expect(failed, 'a deep page must reach its assets').toEqual([])
})

test('gives every page its own description and its translation', async ({ page }) => {
  await page.goto(`${DOCS}/en/getting-started/quickstart/`)

  const description = page.locator('meta[name="description"]')
  await expect(description).toHaveAttribute('content', /.{40,}/)
  await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', /\/en\/getting-started\/quickstart\/$/)
  await expect(page.locator('link[hreflang="de"]')).toHaveAttribute('href', /\/de\/getting-started\/quickstart\/$/)
})

test('keeps the address and the title in step while navigating in the page', async ({ page }) => {
  await page.goto(`${DOCS}/de/cli/`)

  // A sibling in the same group, which is the one the sidebar has open on this page.
  await page.locator('.np-nav', { hasText: 'MCP-Server' }).first().click()
  await expect(page).toHaveURL(`${DOCS}/de/mcp-server/`)
  await expect(page).toHaveTitle(/MCP-Server/)
  await expect(page.locator('.np-nav.active')).toHaveText(/MCP-Server/)

  // The language switch keeps the page and takes the reader to its translation.
  await page.locator('.np-sb-lang button', { hasText: 'EN' }).click()
  await expect(page).toHaveURL(`${DOCS}/en/mcp-server/`)
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')

  await page.goBack()
  await expect(page).toHaveURL(`${DOCS}/de/mcp-server/`)
})

test('forwards the addresses from before the documentation had real ones', async ({ page }) => {
  await page.goto(`${DOCS}/#/de/cli`)
  await expect(page).toHaveURL(`${DOCS}/de/cli/`)

  // Those links did not all name a language; the default one is where they came from.
  await page.goto(`${DOCS}/#/security/hardening`)
  await expect(page).toHaveURL(`${DOCS}/en/security/hardening/`)
})
