import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 16 — Audit Log + Teil 64 — Pagination & Multi-Filter.
 *
 * Hermetic: page.route() mocks only. The /audit page is Admin-only (App.tsx <AdminOnly>);
 * the default MOCK_USER is Admin, so it mounts. The endpoint is cursor-paginated — the page
 * GETs /api/audit?...&take=N and (on "Load more") &afterTs=&afterId=, appending pages
 * (never replacing). The route matches the audit path with a trailing wildcard to catch the
 * query string.
 *
 * Maps to:
 *   - 16.1 — Audit-Log ansehen: rows render with timestamp/action/resource/user/ip columns.
 *   - 16.2 — Filtern: filter inputs update the query (AND-combined) and refetch.
 *   - 16.3 — Sensitive actions visible (CREDENTIAL_CREATED has no secret; LOGIN_FAILED shows user).
 *   - 64.1 — Cursor-Pagination: "Load more" appends the next page; the button disappears on the
 *            last page (nextCursor == null).
 *   - 64.2 — Multi-Filter: action + userId + daterange AND-combined; export href respects the
 *            filter set; the GET query carries all params.
 */

type AuditEntry = {
  id: string;
  timestamp: string;
  userId: string | null;
  username: string | null;
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  details: string | null;
  ipAddress: string | null;
};

function entry(overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    id: 'evt-1',
    timestamp: '2026-06-18T10:00:00.000Z',
    userId: '00000000-0000-0000-0000-000000000099',
    username: 'admin',
    action: 'WORKFLOW_CREATED',
    resourceType: 'Workflow',
    resourceId: '11111111-1111-1111-1111-111111111111',
    details: '{"name":"Deploy"}',
    ipAddress: '10.0.0.42',
    ...overrides,
  };
}

const CURSOR = { timestamp: '2026-06-18T09:00:00.000Z', id: 'evt-50' };

test.describe('Audit Log (Teil 16 + 64)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page); // MOCK_USER = Admin → page mounts
  });

  test('16.1 — renders entries with action/resource/user/ip', async ({ page }) => {
    await page.route('**/api/audit**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          items: [
            entry({ id: 'a1', action: 'WORKFLOW_CREATED', resourceType: 'Workflow', username: 'admin' }),
            entry({ id: 'a2', action: 'LOGIN_FAILED', resourceType: 'User', username: 'mallory', ipAddress: '203.0.113.7', details: '{"reason":"bad password"}' }),
          ],
          nextCursor: null,
        }),
      }),
    );

    await page.goto('/audit');

    await expect(page.getByText('WORKFLOW_CREATED').first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('LOGIN_FAILED').first()).toBeVisible();
    // Column headers present.
    await expect(page.getByText(/^action$|^aktion$/i).first()).toBeVisible();
    await expect(page.getByText(/^resource$|^ressource$/i).first()).toBeVisible();
    // User + IP surfaced on the rows.
    await expect(page.getByText('admin').first()).toBeVisible();
    await expect(page.getByText('mallory')).toBeVisible();
    await expect(page.getByText('203.0.113.7')).toBeVisible();
  });

  test('16.3 — CREDENTIAL_CREATED details are shown WITHOUT a secret; LOGIN_FAILED keeps the username', async ({ page }) => {
    await page.route('**/api/audit**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          items: [
            entry({ id: 'c1', action: 'CREDENTIAL_CREATED', resourceType: 'Credential', username: 'admin', details: '{"name":"prod-svc","username":"svc"}' }),
            entry({ id: 'lf', action: 'LOGIN_FAILED', resourceType: 'User', username: 'eve', details: '{"reason":"invalid credentials"}' }),
          ],
          nextCursor: null,
        }),
      }),
    );

    await page.goto('/audit');
    await expect(page.getByText('CREDENTIAL_CREATED').first()).toBeVisible({ timeout: 15_000 });

    // Expand the credential row to read its details JSON — it must NOT contain a password/secret.
    // The action text also appears as a quick-filter chip, so target the wide row button (it
    // also carries the resource type "Credential" + username, which the chip does not).
    await page.getByRole('button', { name: /CREDENTIAL_CREATED.*Credential/ }).click();
    const details = page.locator('pre').first();
    await expect(details).toBeVisible();
    await expect(details).toContainText(/prod-svc/);
    await expect(details).not.toContainText(/password|secret/i);

    // LOGIN_FAILED row shows the username but the details carry no password.
    await expect(page.getByText('eve')).toBeVisible();
  });

  test('16.2 / 64.2 — filters AND-combine into the query and refetch', async ({ page }) => {
    const seenUrls: string[] = [];
    await page.route('**/api/audit**', (route) => {
      seenUrls.push(route.request().url());
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [entry({ action: 'WORKFLOW_PUBLISHED' })], nextCursor: null }),
      });
    });

    await page.goto('/audit');
    await expect(page.getByText('WORKFLOW_PUBLISHED').first()).toBeVisible({ timeout: 15_000 });

    // Action filter (placeholder WORKFLOW_CREATED) + User ID filter → AND-combined query.
    await page.getByPlaceholder('WORKFLOW_CREATED').fill('WORKFLOW_PUBLISHED');
    await page.locator('input[placeholder="GUID"]').last().fill('admin-guid-123');

    // The query re-fires on debounced state change; poll for a URL carrying BOTH params.
    await expect
      .poll(() => seenUrls.some((u) => /action=WORKFLOW_PUBLISHED/.test(u) && /userId=admin-guid-123/.test(u)), {
        timeout: 10_000,
      })
      .toBe(true);
  });

  test('64.2 — export links carry the active filter set (CSV + NDJSON)', async ({ page }) => {
    await page.route('**/api/audit**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [entry({ action: 'WORKFLOW_PUBLISHED' })], nextCursor: null }),
      }),
    );

    await page.goto('/audit');
    await expect(page.getByText('WORKFLOW_PUBLISHED').first()).toBeVisible({ timeout: 15_000 });

    await page.getByPlaceholder('WORKFLOW_CREATED').fill('WORKFLOW_PUBLISHED');

    // Export is an <a href="/api/audit/export?...&format=..."> built from the same filter params.
    // The links live in a hover-revealed dropdown (display:none until group-hover), so we match
    // by the href attribute directly rather than the accessible-name (hidden els are excluded
    // from the a11y tree). The href is computed synchronously from the filter state.
    const csv = page.locator('a[href*="/api/audit/export"][href*="format=csv"]');
    const ndjson = page.locator('a[href*="/api/audit/export"][href*="format=ndjson"]');
    await expect(csv).toHaveAttribute('href', /\/api\/audit\/export\?.*action=WORKFLOW_PUBLISHED.*format=csv/);
    await expect(ndjson).toHaveAttribute('href', /\/api\/audit\/export\?.*format=ndjson/);
  });

  test('64.1 — "Load more" appends the next page and the button vanishes on the last page', async ({ page }) => {
    let call = 0;
    await page.route('**/api/audit**', (route) => {
      const url = route.request().url();
      const isLoadMore = /afterTs=/.test(url);
      call += 1;
      if (!isLoadMore) {
        // First page: 2 entries + a cursor → "Load more" appears.
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            items: [entry({ id: 'p1-a', action: 'WORKFLOW_CREATED' }), entry({ id: 'p1-b', action: 'WORKFLOW_UPDATED' })],
            nextCursor: CURSOR,
          }),
        });
      }
      // Second page (load more): 1 entry, nextCursor null → button disappears.
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [entry({ id: 'p2-a', action: 'WORKFLOW_DELETED' })], nextCursor: null }),
      });
    });

    await page.goto('/audit');
    await expect(page.getByText('WORKFLOW_CREATED').first()).toBeVisible({ timeout: 15_000 });

    const loadMore = page.getByRole('button', { name: /load more|mehr laden|weitere/i });
    await expect(loadMore).toBeVisible();

    await loadMore.click();

    // New page is appended (old rows remain) — both first-page and second-page actions present.
    await expect(page.getByText('WORKFLOW_DELETED').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('WORKFLOW_UPDATED').first()).toBeVisible(); // first page kept
    // Last page reached → "Load more" gone (nextCursor null).
    await expect(loadMore).toHaveCount(0);
    expect(call).toBeGreaterThanOrEqual(2);
  });
});
