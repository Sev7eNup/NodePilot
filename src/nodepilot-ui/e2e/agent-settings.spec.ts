import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

const limits = {
  enabled: true, powerMode: false, allowServiceIdentity: false, maxConcurrentRuns: 2,
  singleModelCalls: 20, singleToolCalls: 40, singleTimeoutSeconds: 1200,
  teamModelCalls: 100, teamToolCalls: 500, teamDelegations: 20, teamMaxParallelMembers: 3, teamTimeoutSeconds: 1800,
  modelCallTimeoutSeconds: 180, modelMaxOutputTokens: 250000,
  maxContextCharacters: 250000, maxToolOutputCharacters: 16000, maxResultCharacters: 64000, readOnlyMcpTools: [],
};

async function setup(page: Page, count = 30, locked = false, role = 'Admin') {
  await installDefaultMocks(page);
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: 'admin', username: 'admin', role } }));
  let payload = { ...limits };
  const writes: Record<string, unknown>[] = [];
  await page.route('**/api/admin/settings/Agents', route => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON();
      writes.push(body);
      payload = { ...payload, ...Object.fromEntries(Object.entries(body).map(([key, value]) => [key[0].toLowerCase() + key.slice(1), value])) };
    }
    return route.fulfill({ json: { sectionPath: 'Agents', payload, etag: '"v1"',
      effectiveSource: locked ? { PowerMode: 'env', SingleToolCalls: 'env' } : {}, isHotReloadable: false } });
  });
  await page.route('**/api/agents/skills', route => route.fulfill({ json: Array.from({ length: count }, (_, i) => ({
    id: `skill-${i}`, name: `Skill ${i}`, version: '1.0.0', description: `Diagnostic package ${i}`, enabled: i % 2 === 0, sha256: 'a'.repeat(64),
  })) }));
  await page.route('**/api/agents/mcp-servers', route => route.fulfill({ json: Array.from({ length: count }, (_, i) => ({
    id: `server-${i}`, name: `Server ${i}`, transport: 'stdio', enabled: true, command: 'mcp', arguments: [], updatedAt: '',
  })) }));
  await page.goto('/settings?tab=system&section=agents');
  return writes;
}

test('searches bounded registries and expands details without saving', async ({ page }) => {
  const writes = await setup(page);
  const skills = page.getByRole('list', { name: /skill/i });
  const servers = page.getByRole('list', { name: /mcp/i });
  for (const list of [skills, servers]) {
    await expect(list.getByRole('listitem')).toHaveCount(30);
    expect(await list.evaluate(el => el.clientHeight)).toBeLessThanOrEqual(320);
    expect(await list.evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);
  }
  await page.getByRole('searchbox', { name: /skill/i }).fill('DIAGNOSTIC PACKAGE 29');
  await expect(skills.getByRole('listitem')).toHaveCount(1);
  await expect(skills.getByText('Diagnostic package 29')).toBeHidden();
  await skills.getByRole('button', { name: /details/i }).click();
  await expect(skills.getByText('Diagnostic package 29')).toBeVisible();
  await expect(skills.getByRole('button', { name: /details/i })).toHaveAttribute('aria-expanded', 'true');
  await page.getByRole('searchbox', { name: /mcp/i }).fill('server-29');
  await expect(servers.getByRole('listitem')).toHaveCount(1);
  await servers.getByRole('button', { name: /details/i }).click();
  await expect(servers.getByText('ID: server-29')).toBeVisible();
  await page.getByRole('searchbox', { name: /skill/i }).fill('missing');
  await expect(page.getByText(/no matching entries|keine passenden einträge/i)).toBeVisible();
  expect(writes).toHaveLength(0);
});

test('saves power mode, retains limits and restores normal editing', async ({ page }) => {
  const writes = await setup(page, 1);
  const power = page.getByRole('switch', { name: /power/i });
  await expect(power).not.toBeChecked();
  await page.locator('#agent-limit-singleToolCalls').fill('55');
  await power.check();
  for (const key of ['singleModelCalls', 'singleToolCalls', 'singleTimeoutSeconds', 'teamModelCalls', 'teamToolCalls', 'teamDelegations', 'teamTimeoutSeconds']) {
    await expect(page.locator(`#agent-limit-${key}`)).toBeDisabled();
  }
  await expect(page.locator('#agent-limit-modelCallTimeoutSeconds')).toBeEnabled();
  await expect(page.locator('#agent-limit-teamMaxParallelMembers')).toBeEnabled();
  await page.getByRole('button', { name: /^save$|^speichern$/i }).click();
  await expect.poll(() => writes.at(-1)?.PowerMode).toBe(true);
  expect(writes.at(-1)?.SingleToolCalls).toBe(55);
  await page.reload();
  await expect(power).toBeChecked();
  await power.uncheck();
  await expect(page.locator('#agent-limit-singleToolCalls')).toBeEnabled();
  await expect(page.locator('#agent-limit-singleToolCalls')).toHaveValue('55');
  await page.getByRole('button', { name: /^save$|^speichern$/i }).click();
  await expect.poll(() => writes.at(-1)?.PowerMode).toBe(false);
});

test('keeps environment locks and empty states', async ({ page }) => {
  await setup(page, 0, true);
  await expect(page.getByRole('switch', { name: /power/i })).toBeDisabled();
  await expect(page.locator('#agent-limit-singleToolCalls')).toBeDisabled();
  await expect(page.getByText(/no entries yet|noch keine einträge/i)).toHaveCount(2);
});

test('toggles MCP servers directly while preserving destination and stored secrets', async ({ page }) => {
  await setup(page, 0);
  let server = { id: 'server-toggle', name: 'Toggle MCP', enabled: true, transport: 'stdio', command: 'C:/tools/mcp.exe',
    arguments: ['--read-only'], endpoint: null, hasSecrets: true, updatedAt: '2026-10-07T12:00:00Z' };
  const saved: Record<string, unknown>[] = [];
  let conflict = false;
  await page.route('**/api/agents/mcp-servers', route => route.fulfill({ json: [server] }));
  await page.route('**/api/agents/mcp-servers/server-toggle', route => {
    const body = route.request().postDataJSON();
    saved.push(body);
    if (conflict) return route.fulfill({ status: 409, json: { message: 'MCP server was changed. Reload before saving.' } });
    server = { ...server, enabled: body.enabled, updatedAt: '2026-10-07T12:01:00Z' };
    return route.fulfill({ json: server });
  });
  await page.reload();
  const enabled = page.getByRole('list', { name: /mcp/i }).getByRole('checkbox', { name: /enabled|aktiviert/i });
  await expect(enabled).toBeChecked();
  await enabled.click();
  await expect(enabled).not.toBeChecked();
  expect(saved[0]).toEqual({ name: 'Toggle MCP', enabled: false, transport: 'stdio', command: 'C:/tools/mcp.exe',
    arguments: ['--read-only'], endpoint: null, updatedAt: '2026-10-07T12:00:00Z' });
  await expect(enabled).toBeEnabled();
  await enabled.click();
  await expect(enabled).toBeChecked();
  expect(saved[1].updatedAt).toBe('2026-10-07T12:01:00Z');
  expect(saved[1]).not.toHaveProperty('secrets');
  await expect(enabled).toBeEnabled();
  conflict = true;
  await enabled.click();
  await expect(page.getByRole('alert')).toContainText('MCP server was changed');
  await expect(enabled).toBeChecked();
});

test('opens skill import explicitly and closes it after importing or cancelling', async ({ page }) => {
  await setup(page, 0);
  let imported: Record<string, unknown> | undefined;
  await page.route('**/api/agents/skills', route => {
    if (route.request().method() === 'POST') imported = route.request().postDataJSON();
    return route.fulfill({ json: [] });
  });
  const open = page.getByRole('button', { name: /import skill package|skill-?paket importieren/i });
  await expect(page.locator('input[type="file"]')).toHaveCount(0);
  await expect(open).toBeEnabled();
  await open.click();
  const form = page.getByRole('form', { name: /import skill package|skill-?paket importieren/i });
  const submit = form.getByRole('button', { name: /^import$|^importieren$/i });
  await expect(submit).toBeDisabled();
  await form.screenshot({ path: 'test-results/agent-settings-skill-import.png' });
  await form.locator('input[type="file"]').setInputFiles({ name: 'skill.zip', mimeType: 'application/zip', buffer: Buffer.from('test archive') });
  await form.getByRole('textbox', { name: /version/i }).fill('2.0.0');
  await expect(submit).toBeEnabled();
  await submit.click();
  await expect(form).toHaveCount(0);
  expect(imported?.version).toBe('2.0.0');
  expect(imported?.package).toBe(Buffer.from('test archive').toString('base64'));
  await open.click();
  await expect(form.getByRole('textbox', { name: /version/i })).toHaveValue('1.0.0');
  await expect(submit).toBeDisabled();
  await form.getByRole('button', { name: /cancel|abbrechen/i }).click();
  await expect(form).toHaveCount(0);
});

test('operators cannot access system settings through a deep link', async ({ page }) => {
  await setup(page, 1, false, 'Operator');
  await expect(page.getByRole('heading', { name: /personal|persönlich/i }).first()).toBeVisible();
  await expect(page.getByRole('list', { name: /skill/i })).toHaveCount(0);
  await expect(page.getByRole('switch', { name: /power/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^delete$|^löschen$|^edit$|^bearbeiten$/i })).toHaveCount(0);
});

for (const width of [390, 768, 1440]) {
  for (const theme of ['light', 'dark']) {
    test(`layout and help at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1000 });
      await page.addInitScript(({ theme }) => {
        localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme }, version: 0 }));
        localStorage.setItem('nodepilot.lang', theme === 'dark' ? 'de' : 'en');
      }, { theme });
      await setup(page);
      await expect(page.locator('html')).toHaveAttribute('data-skin', theme);
      await expect(page.getByRole('switch', { name: /power/i })).toBeVisible();
      const fields = page.locator('[id^="agent-limit-"]:is(input)');
      await expect(fields).toHaveCount(16);
      for (const field of await fields.all()) {
        const help = await field.getAttribute('aria-describedby');
        await expect(page.locator(`#${help}`)).not.toBeEmpty();
      }
      const card = page.getByRole('list', { name: /skill/i });
      await page.getByRole('switch', { name: /power/i }).scrollIntoViewIfNeeded();
      await page.screenshot({ path: `test-results/agent-settings-limits-${width}-${theme}.png` });
      await card.scrollIntoViewIfNeeded();
      const box = await card.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.x + box!.width).toBeLessThanOrEqual(width);
      await page.screenshot({ path: `test-results/agent-settings-${width}-${theme}.png`, fullPage: true });
    });
  }
}
