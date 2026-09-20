import { expect, test, type ConsoleMessage, type Page, type Request } from '@playwright/test';

/**
 * Smoke test for the published browser demo.
 *
 * It runs against the built `dist-demo`, served under a sub-path, because that is the shape
 * the site publishes and the only place the base-path failures show up.
 *
 * The two blanket assertions carry most of the value: no console errors on any reachable
 * route, and no `[demo] unhandled` line. The second turns "a page renders subtly wrong
 * because the fallback guessed" from an open-ended class of bugs into a test failure.
 */

const ROUTES = [
  '#/',
  '#/workflows',
  '#/executions',
  '#/operations',
  '#/machines',
  '#/global-variables',
  '#/custom-activities',
  '#/maintenance-windows',
  '#/alerts',
  '#/alerts?tab=system',
  '#/ai-chat',
  '#/support-log',
  '#/database',
  '#/backup',
  // All ten sections, not just the first: each one loads its own endpoints, and a route walk
  // only ever catches what a page requests on load.
  '#/metrics/mission-control',
  '#/metrics/workflows',
  '#/metrics/activities',
  '#/metrics/winrm',
  '#/metrics/triggers',
  '#/metrics/api',
  '#/metrics/runtime',
  '#/metrics/security',
  '#/metrics/ai',
  '#/metrics/database',
  '#/users',
  '#/audit',
  '#/settings',
  '#/settings?tab=system&section=integrations',
  '#/settings?tab=system&section=ai-knowledge',
  '#/settings?tab=system&section=authentication',
  '#/settings?tab=system&section=security',
  '#/settings?tab=system&section=logging',
  '#/settings?tab=system&section=performance',
  '#/settings?tab=system&section=database',
  '#/settings?tab=system&section=retention',
  '#/settings?tab=system&section=system-info',
];

interface Watcher {
  errors: string[];
  unhandled: string[];
  offOrigin: string[];
  hubCalls: string[];
}

function watch(page: Page): Watcher {
  const state: Watcher = { errors: [], unhandled: [], offOrigin: [], hubCalls: [] };

  page.on('console', (message: ConsoleMessage) => {
    const text = message.text();
    if (text.includes('[demo] unhandled')) state.unhandled.push(text);
    if (message.type() === 'error') state.errors.push(text);
  });
  page.on('pageerror', (error) => state.errors.push(String(error)));
  page.on('request', (request: Request) => {
    const url = new URL(request.url());
    if (url.origin !== '127.0.0.1:4180' && url.host !== '127.0.0.1:4180') state.offOrigin.push(request.url());
    if (url.pathname.startsWith('/hubs/')) state.hubCalls.push(request.url());
  });

  return state;
}

/**
 * Same-origin anchors whose target lies outside the demo's own directory.
 *
 * These are the links that silently take a visitor out of the demo: a plain `<a href="/api/...">`
 * is a document navigation, so the fetch patch never sees it, and on a sub-path deployment it
 * lands on the host's 404 with the in-memory world gone.
 */
async function escapingAnchors(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const base = new URL('./', document.baseURI).pathname;
    return Array.from(document.querySelectorAll('a[href]'))
      .filter((a) => {
        const anchor = a as HTMLAnchorElement;
        if (anchor.target && anchor.target !== '_self') return false;
        const url = new URL(anchor.href, document.baseURI);
        return url.origin === window.location.origin && !url.pathname.startsWith(base);
      })
      .map((a) => (a as HTMLAnchorElement).getAttribute('href') ?? '(no href)');
  });
}

/**
 * Sources of every <img> that failed to load.
 *
 * A broken brand mark is invisible to a console-error check: a 404 image logs nothing the page
 * observes, it just quietly renders its alt text.
 */
async function brokenImages(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll('img'))
      .filter((img) => !img.complete || img.naturalWidth === 0)
      .map((img) => img.getAttribute('src') ?? '(no src)'),
  );
}

/**
 * Waits until the demo backend has gone quiet *after* doing work.
 *
 * Two traps this avoids. `waitForLoadState('networkidle')` is blind, because the patched fetch
 * never reaches the network — Playwright sees zero requests and reports idle immediately. And
 * "nothing in flight" alone is satisfied at t=0, before React has issued its first request, so
 * a page that crashes a moment later still looks clean.
 *
 * So: wait for activity, then for the completed counter to stop moving with nothing pending.
 */
async function settle(page: Page): Promise<void> {
  const counters = () => page.evaluate(() => {
    const w = window as { __npDemoPending?: number; __npDemoCompleted?: number };
    return { pending: w.__npDemoPending ?? 0, completed: w.__npDemoCompleted ?? 0 };
  });

  await page.waitForFunction(
    () => ((window as { __npDemoCompleted?: number }).__npDemoCompleted ?? 0) > 0,
    null,
    { timeout: 15_000 },
  );

  let stable = 0;
  let previous = -1;
  for (let i = 0; i < 80 && stable < 3; i++) {
    const { pending, completed } = await counters();
    if (pending === 0 && completed === previous) stable += 1;
    else stable = 0;
    previous = completed;
    if (stable < 3) await page.waitForTimeout(100);
  }
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

/**
 * True when any error UI is on screen.
 *
 * Deliberately matched on page text rather than a `[role="alert"]` container: the app's own
 * ErrorBoundary uses that role, but React Router's error element does not, and scoping to the
 * role let a crashed Alerting page pass as clean.
 */
async function hasErrorUi(page: Page): Promise<boolean> {
  return page.evaluate(() =>
    /Unerwarteter Fehler|Unexpected Application Error/i.test(document.body.innerText),
  );
}

/** Reads the demo's own backend from inside the page, which is deterministic. */
async function api<T>(page: Page, path: string): Promise<T> {
  return page.evaluate(async (p) => {
    const response = await fetch(p);
    return (await response.json()) as unknown;
  }, path) as Promise<T>;
}

test.describe('published browser demo', () => {
  test('boots with sample data and resolves its assets under the sub-path', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./');

    await expect(page.getByText(/Dashboard|Übersicht/).first()).toBeVisible();

    // EVERY image, not just the first one in the sidebar. The brand mark renders in several
    // places (sidebar, designer header) from literals that are root-absolute in the product;
    // asserting on one of them left the others unguarded.
    const broken = await brokenImages(page);
    expect(broken, `broken images: ${broken.join(', ')}`).toEqual([]);
    expect(await page.locator('img').count()).toBeGreaterThan(0);

    // The documentation sits beside the demo on Pages, not above it.
    const docsHref = await page.locator('a[href*="docs"]').first().getAttribute('href');
    expect(docsHref).toBe('../docs/');

    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  test('opens in the browser language on the Minimal Dark skin', async ({ page }) => {
    // Checked on a first visit, with storage cleared: the product falls back to German and to
    // the OS theme, which would make the shop window look different to every visitor. Both are
    // seeded only when nothing is stored, so a visitor's own choice still survives a reload.
    // The runner's browser asks for English, so this is the English half of the rule.
    await page.context().clearCookies();
    await page.goto('./');
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await settle(page);

    expect(await page.evaluate(() => navigator.languages[0])).toMatch(/^en/);
    expect(await page.evaluate(() => document.documentElement.lang)).toBe('en');
    expect(await page.evaluate(() => document.documentElement.getAttribute('data-skin'))).toBe('dark-minimal');
    // The demo's own chrome follows the same choice; it used to read navigator.language and sat
    // in German beside an English app.
    await expect(page.locator('.np-demo-bar__message')).toContainText('Simulated data');
  });

  test('opens in German for a German browser, like the website and the docs', async ({ browser }) => {
    // The other half: the demo used to pin English whatever the browser asked for, so a German
    // visitor got a German website and documentation next to an English demo.
    const context = await browser.newContext({ locale: 'de-DE' });
    const page = await context.newPage();
    await page.goto('./');
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await settle(page);

    expect(await page.evaluate(() => document.documentElement.lang)).toBe('de');
    await expect(page.locator('.np-demo-bar__message')).toContainText('Simulierte Daten');
    await context.close();
  });

  test('walks every reachable route without console errors or unhandled endpoints', async ({ page }) => {
    // Every route is a full document load against the built bundle, so the walk needs more
    // than the per-test default.
    test.slow();
    const seen = watch(page);
    await page.goto('./');

    const crashed: string[] = [];
    const brokenAssets: string[] = [];
    const escaping: string[] = [];
    for (const [index, route] of ROUTES.entries()) {
      // A distinct query per route forces a real document load. Changing only the fragment
      // is a same-document navigation, so React may not have mounted the new route before
      // the check runs — and the walk would report a crashed page as clean.
      await page.goto(`./?route=${index}${route}`);
      await settle(page);
      // An error boundary is why a crash can be invisible here: React hands the error to
      // the boundary instead of the window, so `pageerror` never fires and a route that
      // throws still finishes "clean". Assert the boundary is absent, not just the console.
      if (await hasErrorUi(page)) crashed.push(route);
      // A 404 image logs nothing; it silently renders its alt text instead.
      for (const src of await brokenImages(page)) brokenAssets.push(`${route} -> ${src}`);
      // A link that leaves the demo is a one-way door: the host answers 404 and the
      // in-memory world is rebuilt from seed when the visitor comes back.
      for (const href of await escapingAnchors(page)) escaping.push(`${route} -> ${href}`);
    }

    expect(crashed, `routes rendering an error boundary: ${crashed.join(', ')}`).toEqual([]);
    expect(brokenAssets, `broken images: ${brokenAssets.join(', ')}`).toEqual([]);
    // An anchor pointing at /api is one the demo's click guard can intercept and answer.
    // Anything else leaving the demo base is a genuine one-way door with nothing to catch it.
    const unguarded = escaping.filter((entry) => !entry.includes('-> /api/'));
    expect(unguarded, `links leaving the demo: ${unguarded.join(', ')}`).toEqual([]);
    expect(seen.unhandled, seen.unhandled.join('\n')).toEqual([]);
    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });


  /**
   * Routes whose primary affordance is opening an editor for an existing row.
   *
   * A route walk cannot reach these: the crash lives behind a button. Custom Nodes is the case
   * that surfaced it — `GET /custom-activities/:id` was unserved, the fallback answered `{}`,
   * and `form.name.trim()` took the page down the moment Edit was clicked.
   *
   * Only pages the seeded world gives rows. Maintenance windows and alert rules are empty by
   * design (both are opt-in by data in the product), so there is nothing to edit there.
   */
  const EDITABLE_ROUTES = [
    '#/machines',
    '#/global-variables',
    '#/custom-activities',
    '#/users',
  ];

  test('opens the row editor on every page that has one', async ({ page }) => {
    const seen = watch(page);
    const crashed: string[] = [];
    const missing: string[] = [];

    for (const [index, route] of EDITABLE_ROUTES.entries()) {
      await page.goto(`./?editor=${index}${route}`);
      await settle(page);

      const opened = await page.evaluate(() => {
        const buttons = Array.from(document.querySelectorAll('button'));
        const edit = buttons.find((b) =>
          /^(Bearbeiten|Edit)$/i.test((b.textContent || '').trim())
          || /bearbeiten|edit/i.test(b.getAttribute('aria-label') || '')
          || /bearbeiten|edit/i.test(b.title || ''));
        if (!edit) return false;
        edit.click();
        return true;
      });

      if (!opened) { missing.push(route); continue; }
      await settle(page);
      if (await hasErrorUi(page)) crashed.push(route);
      await page.keyboard.press('Escape');
    }

    expect(crashed, `row editors that crashed: ${crashed.join(', ')}`).toEqual([]);
    // Not an assertion about the pages, but about this test: a route where the button cannot be
    // found is a route this guard silently stopped covering.
    expect(missing, `no edit affordance found on: ${missing.join(', ')}`).toEqual([]);
    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  /**
   * Opening an editor proves nothing about saving, and saving is where the gaps were: a page
   * whose write endpoint is unrouted looks perfectly healthy until the button is pressed.
   * These go through the real form, so the body shape is the one the product sends.
   */
  const SAVE_ROUTES = [
    { route: '#/machines', field: 'name' },
    { route: '#/global-variables', field: 'name' },
  ];

  test('saves an edited row on every page whose editor can be reached', async ({ page }) => {
    const seen = watch(page);
    const failed: string[] = [];

    for (const [index, { route, field }] of SAVE_ROUTES.entries()) {
      const edited = `Edited ${index}`;
      await page.goto(`./?save=${index}${route}`);
      await settle(page);

      await page.evaluate(() => {
        const edit = Array.from(document.querySelectorAll('button')).find((b) =>
          /^(Bearbeiten|Edit)$/i.test((b.textContent || '').trim())
          || /bearbeiten|edit/i.test(b.getAttribute('aria-label') || '')
          || /bearbeiten|edit/i.test(b.title || ''));
        edit?.click();
      });
      await settle(page);

      const saved = await page.evaluate(({ value, name }) => {
        const panel = document.querySelector('.np-modal-panel');
        if (!panel) return 'no editor opened';
        const input = panel.querySelector<HTMLInputElement>(`input[name="${name}"]`)
          ?? panel.querySelector<HTMLInputElement>('input[type="text"]');
        if (!input) return 'no text field in the editor';
        // React listens to the input event, so the native setter has to be used for the
        // component's state to follow.
        const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
        setter.call(input, value);
        input.dispatchEvent(new Event('input', { bubbles: true }));
        // Each page labels its primary action differently: Save, Update or Create.
        const submit = Array.from(panel.querySelectorAll('button')).find((b) =>
          /^(Speichern|Save|Aktualisieren|Update|Anlegen|Create)$/i.test((b.textContent || '').trim()));
        if (!submit) return 'no save button in the editor';
        submit.click();
        return 'ok';
      }, { value: edited, name: field });

      if (saved !== 'ok') { failed.push(`${route}: ${saved}`); continue; }
      await settle(page);
      if (await hasErrorUi(page)) { failed.push(`${route}: error UI after save`); continue; }
      // The row has to carry the new value — a refused write leaves the old one standing.
      if (!(await page.getByText(edited, { exact: false }).first().isVisible().catch(() => false))) {
        failed.push(`${route}: the edited value never reached the list`);
      }
    }

    expect(failed, failed.join('\n')).toEqual([]);
    expect(seen.unhandled, seen.unhandled.join('\n')).toEqual([]);
    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  /**
   * The defects a route walk and a single save cannot see: a rollback, a move, a recursive
   * delete and a refused download. Driven through the page so they run against the built
   * bundle and its patched fetch, not against the sources.
   */
  test('handles the write paths no page-load or save can reach', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./#/global-variables');
    await settle(page);

    const result = await page.evaluate(async () => {
      const call = async (method: string, path: string, body?: unknown) => {
        const response = await fetch(`/api${path}`, {
          method,
          headers: body ? { 'content-type': 'application/json' } : undefined,
          body: body ? JSON.stringify(body) : undefined,
        });
        const text = await response.text();
        return { status: response.status, body: text ? JSON.parse(text) : null };
      };

      // A folder rename has to carry its descendants with it.
      const parent = await call('POST', '/global-variable-folders', { name: 'E2E parent' });
      const child = await call('POST', '/global-variable-folders', {
        name: 'E2E child', parentFolderId: parent.body.id,
      });
      await call('PUT', `/global-variable-folders/${parent.body.id}`, { name: 'E2E renamed' });
      const afterRename = await call('GET', '/global-variable-folders');
      const renamedChild = afterRename.body.find((f: { id: string }) => f.id === child.body.id);

      // A variable dragged onto a folder has to land in it, and a recursive delete has to
      // take both with it.
      const variables = await call('GET', '/global-variables');
      const variableId = variables.body[0].id;
      await call('POST', `/global-variables/${variableId}/move-folder`, { folderId: child.body.id });
      const moved = (await call('GET', '/global-variables')).body
        .find((g: { id: string }) => g.id === variableId);
      const removed = await call('DELETE', `/global-variable-folders/${parent.body.id}?recursive=true`);
      const afterDelete = await call('GET', '/global-variables');

      // A rollback has to restore, not report success and change nothing.
      const activities = await call('GET', '/custom-activities?includeDisabled=true');
      const activityId = activities.body[0].id;
      const before = (await call('GET', `/custom-activities/${activityId}`)).body.scriptTemplate;
      await call('PUT', `/custom-activities/${activityId}`, { scriptTemplate: 'Write-Output "e2e"' });
      const versions = await call('GET', `/custom-activities/${activityId}/versions`);
      await call('POST', `/custom-activities/${activityId}/rollback/${versions.body[0].version}`);
      const restored = (await call('GET', `/custom-activities/${activityId}`)).body.scriptTemplate;

      const download = await call('GET', '/diagnostics/support-log/download?date=2026-09-18');

      return {
        childPath: renamedChild?.path,
        movedFolderId: moved?.folderId === child.body.id,
        deletedFolders: removed.body?.deletedFolders,
        variableGone: !afterDelete.body.some((g: { id: string }) => g.id === variableId),
        rolledBack: restored === before,
        downloadStatus: download.status,
        downloadCode: download.body?.code,
      };
    });

    expect(result.childPath).toBe('/E2E renamed/E2E child');
    expect(result.movedFolderId).toBe(true);
    expect(result.deletedFolders).toBe(2);
    expect(result.variableGone).toBe(true);
    expect(result.rolledBack).toBe(true);
    expect(result.downloadStatus).toBe(501);
    expect(result.downloadCode).toBe('DEMO_NOT_AVAILABLE');
    expect(seen.unhandled, seen.unhandled.join('\n')).toEqual([]);
  });

  test('keeps the audit export inside the demo', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./#/audit');
    await settle(page);

    // A plain `<a href="/api/...">` never reaches the fetch patch — it is a document
    // navigation. Unguarded it lands on the host's 404 and the in-memory world is gone.
    const csv = page.locator('a[href*="/api/audit/export"][href*="csv"]').first();
    await expect(csv).toHaveCount(1);

    // Asserted on navigation rather than the download event: a blob download does not
    // reliably surface as one, while 'the visitor is still in the demo' is the guarantee.
    const before = page.url();
    const saved = await page.evaluate(async () => {
      const anchor = document.querySelector('a[href*="/api/audit/export"]') as HTMLAnchorElement;
      let triggered = false;
      const create = document.createElement.bind(document);
      document.createElement = ((tag: string) => {
        const element = create(tag);
        if (tag === 'a') {
          const click = (element as HTMLAnchorElement).click.bind(element);
          (element as HTMLAnchorElement).click = () => { if ((element as HTMLAnchorElement).download) triggered = true; click(); };
        }
        return element;
      }) as typeof document.createElement;
      anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
      await new Promise((resolve) => setTimeout(resolve, 1_500));
      document.createElement = create;
      return triggered;
    });

    expect(saved, 'the export produced no download').toBe(true);
    expect(page.url()).toBe(before);
    await expect(page.locator('aside').first()).toBeVisible();
    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  test('keeps every request on its own origin and never negotiates SignalR', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./');
    await page.goto('./#/workflows');
    await settle(page);
    // Give the health poll and the hub retry loop a chance to fire.
    await page.waitForTimeout(3_000);

    expect(seen.offOrigin, seen.offOrigin.join('\n')).toEqual([]);
    // The fake hub is injected, so nothing should reach /hubs/ over HTTP; without it the
    // retry loop logs a failed negotiate every 30 seconds for the whole visit.
    expect(seen.hubCalls, seen.hubCalls.join('\n')).toEqual([]);
  });

  test('renders the settings sections with real values', async ({ page }) => {
    const seen = watch(page);
    // The System tab, not the page default: the crash was behind it, which is why a route
    // walk that only opened /settings reported everything as fine.
    await page.goto('./#/settings?tab=system&section=integrations');
    await settle(page);

    // The cards replace their fallback with the server payload and index into
    // effectiveSource without an optional chain, so a partial envelope throws.
    await expect.poll(async () => page.evaluate(() =>
      Array.from(document.querySelectorAll('input')).some((i) => i.value === 'smtp.contoso.example'),
    ), { timeout: 15_000 }).toBe(true);

    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  test('shows existing runs on the Live-Ops timeline', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./#/operations');
    await settle(page);

    // Every identifier in the snapshot has to be keyed the way OperationsGraph declares it;
    // a row with the wrong key is filtered out silently and the console looks idle.
    const snapshot = await page.evaluate(async () => {
      const response = await fetch('/api/operations/graph?windowMinutes=60');
      return (await response.json()) as { nodes: { workflowId: string }[]; recent: { executionId: string }[] };
    });
    expect(snapshot.nodes.length).toBeGreaterThan(0);
    expect(snapshot.nodes.every((n) => !!n.workflowId)).toBe(true);
    // A 30-minute window is what Live-Ops opens on; a seed whose newest run is older than
    // that shows a visitor an empty console on their first look.
    const half = await page.evaluate(async () => {
      const response = await fetch('/api/operations/graph?windowMinutes=30');
      return (await response.json()) as { recent: unknown[] };
    });
    expect(half.recent.length).toBeGreaterThan(0);
    expect(snapshot.recent.length).toBeGreaterThan(0);
    expect(snapshot.recent.every((r) => !!r.executionId)).toBe(true);

    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  test('runs the full lifecycle: edit, publish, run', async ({ page }) => {
    const seen = watch(page);
    await page.goto('./#/workflows');

    const workflows = await api<{ id: string; name: string; version: number }[]>(page, '/api/workflows');
    const target = workflows[0];
    await page.goto(`./#/workflows/${target.id}`);
    await expect(page.getByRole('button', { name: /^(Edit|Bearbeiten)$/ })).toBeVisible();

    // Locking disables the workflow, atomically, exactly as the product does.
    await page.getByRole('button', { name: /^(Edit|Bearbeiten)$/ }).click();
    await expect.poll(async () => {
      const locked = await api<{ isEnabled: boolean; checkedOutByUserId: string | null }>(
        page, `/api/workflows/${target.id}`,
      );
      return locked.isEnabled === false && locked.checkedOutByUserId !== null;
    }, { timeout: 15_000 }).toBe(true);

    // Running is refused while disabled, so publishing is a required step and not a detour.
    // Asserted on the effect rather than the toast: a toast auto-dismisses and would make
    // this race, while 'no run was started' is exactly the guarantee that matters.
    const runsBefore = await api<{ total: number }>(page, `/api/executions?workflowId=${target.id}`);
    await page.getByRole('button', { name: /^(Test run|Test-Run)$/ }).click();
    await page.waitForTimeout(1_000);
    const runsWhileDisabled = await api<{ total: number }>(page, `/api/executions?workflowId=${target.id}`);
    expect(runsWhileDisabled.total).toBe(runsBefore.total);

    await page.getByRole('button', { name: /^(Publish|Veröffentlichen)$/ }).click();
    await expect.poll(async () => {
      const after = await api<{ isEnabled: boolean; version: number }>(page, `/api/workflows/${target.id}`);
      return after.isEnabled && after.version > target.version;
    }, { timeout: 15_000 }).toBe(true);

    // The refusal toast overlays the header while it is up, so a click here can land on the
    // toast instead of the button. Waiting it out is the difference between testing the run
    // and testing nothing: with no run started, the newest row is still a seeded one.
    await page.locator('[role="status"]').last()
      .waitFor({ state: 'hidden', timeout: 15_000 })
      .catch(() => { /* already gone */ });

    const beforeRun = await api<{ total: number }>(page, `/api/executions?workflowId=${target.id}`);
    await page.getByRole('button', { name: /^(Test run|Test-Run)$/ }).click();

    // Assert the click started something before waiting on the outcome: a run that never
    // starts and a run that never finishes would otherwise fail identically.
    await expect.poll(async () => {
      const runs = await api<{ total: number }>(page, `/api/executions?workflowId=${target.id}`);
      return runs.total;
    }, { timeout: 20_000 }).toBe(beforeRun.total + 1);

    // The canvas animation and the live console are driven by the fake hub, so a finished run
    // proves the group routing works end to end.
    await expect.poll(async () => {
      const runs = await api<{ items: { status: string }[] }>(page, `/api/executions?workflowId=${target.id}`);
      return runs.items[0]?.status;
    }, { timeout: 60_000 }).toBe('Succeeded');

    expect(seen.errors, seen.errors.join('\n')).toEqual([]);
  });

  test('restores the sample data on reload', async ({ page }) => {
    await page.goto('./#/workflows');
    const before = await api<{ id: string; version: number }[]>(page, '/api/workflows');

    await page.evaluate(async (id) => {
      await fetch(`/api/workflows/${id}/lock`, { method: 'POST' });
      await fetch(`/api/workflows/${id}/publish`, {
        method: 'POST',
        body: JSON.stringify({ name: 'Changed in this tab' }),
      });
    }, before[0].id);

    await page.reload();
    const after = await api<{ id: string; name: string; version: number }[]>(page, '/api/workflows');
    expect(after[0].version).toBe(before[0].version);
    expect(after[0].name).not.toBe('Changed in this tab');
  });

  test('keeps two tabs of the same browser apart', async ({ context }) => {
    const tabA = await context.newPage();
    await tabA.goto('./#/workflows');
    const seenA = watch(tabA);

    const target = (await api<{ id: string; version: number }[]>(tabA, '/api/workflows'))[0];
    await tabA.evaluate(async (id) => {
      await fetch(`/api/workflows/${id}/lock`, { method: 'POST' });
      await fetch(`/api/workflows/${id}/publish`, { method: 'POST', body: JSON.stringify({ name: 'Tab A edit' }) });
    }, target.id);

    // Opening and reloading a second tab publishes an identity event. Without the isolation
    // guard every other tab answers it by clearing its caches and remounting, which discards
    // unsaved editor state.
    const tabB = await context.newPage();
    await tabB.goto('./#/workflows');
    await tabB.reload();
    await tabB.waitForLoadState('networkidle');

    const inB = (await api<{ name: string; version: number }[]>(tabB, '/api/workflows'))[0];
    expect(inB.name).not.toBe('Tab A edit');
    expect(inB.version).toBe(target.version);

    const inA = (await api<{ name: string }[]>(tabA, '/api/workflows'))[0];
    expect(inA.name).toBe('Tab A edit');
    expect(seenA.errors, seenA.errors.join('\n')).toEqual([]);

    await tabA.close();
    await tabB.close();
  });
});
