import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 1 — Workflow-Management (create / save / rename / list round-trip).
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * The preview build resolves i18n from the browser locale (renders EN here), so selectors
 * use bilingual regexes. A freshly created workflow is auto-checked-out by its creator, so
 * the editor opens in State B (locked-by-me) with the name field + Save/Publish visible.
 */

const NEW_ID = 'c1c1c1c1-1111-1111-1111-111111111111';

function workflowJson(overrides: Record<string, unknown> = {}) {
  return {
    id: NEW_ID,
    name: 'E2E_Basic_Test',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked-by-me → State B (editable)
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    activityCount: 0,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

test.describe('Workflow-Management (Teil 1)', () => {
  test('1.1 — create new workflow opens the editor on its name', async ({ page }) => {
    await installDefaultMocks(page);
    let created: ReturnType<typeof workflowJson> | null = null;
    await page.route('**/api/workflows', (route) => {
      if (route.request().method() === 'POST') {
        const body = route.request().postDataJSON() as { name: string };
        created = workflowJson({ name: body.name });
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });
    await page.route(`**/api/workflows/${NEW_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(created ?? workflowJson()) }),
    );

    await page.goto('/workflows');
    await page.getByRole('button', { name: /new workflow|neuer workflow/i }).click();
    await page.getByPlaceholder(/workflow.?name|name/i).first().fill('E2E_Basic_Test');
    await page.getByRole('button', { name: /^(create|erstellen|anlegen)$/i }).click();

    await expect(page).toHaveURL(new RegExp(`/workflows/${NEW_ID}`));
    await expect(page.getByRole('textbox', { name: /workflow.?name/i })).toHaveValue('E2E_Basic_Test', { timeout: 15_000 });
  });

  test('1.2 — Save (Save in place) issues a PUT and the workflow persists', async ({ page }) => {
    await installDefaultMocks(page);
    let putBody: Record<string, unknown> | null = null;
    await page.route(`**/api/workflows/${NEW_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson()) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson()) });
    });

    await page.goto(`/workflows/${NEW_ID}`);
    const save = page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();
    await expect(save).toBeVisible({ timeout: 15_000 });
    await save.click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
  });

  test('1.3 — rename in the editor sends the new name on save', async ({ page }) => {
    await installDefaultMocks(page);
    let putBody: { name?: string } | null = null;
    await page.route(`**/api/workflows/${NEW_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ name: putBody?.name })) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson()) });
    });

    await page.goto(`/workflows/${NEW_ID}`);
    const nameField = page.getByRole('textbox', { name: /workflow.?name/i });
    await expect(nameField).toHaveValue('E2E_Basic_Test', { timeout: 15_000 });
    await nameField.fill('E2E_Renamed_Test');
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody?.name, { timeout: 10_000 }).toBe('E2E_Renamed_Test');
  });

  test('1.4 — workflow appears in the list and opens from there', async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([workflowJson({ name: 'E2E_Renamed_Test', checkedOutByUserId: null, checkedOutByUserName: null, isEnabled: true })]),
      }),
    );
    await page.route(`**/api/workflows/${NEW_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ name: 'E2E_Renamed_Test' })) }),
    );

    await page.goto('/workflows');
    const row = page.getByRole('button', { name: 'E2E_Renamed_Test' });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.click();
    await expect(page).toHaveURL(new RegExp(`/workflows/${NEW_ID}`));
  });
});
