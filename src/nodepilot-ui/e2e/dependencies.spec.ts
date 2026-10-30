import { test, expect, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { installDefaultMocks } from './fixtures/mockApi';
import type { DependencyObject, DependencyLeg, DependencyReport } from '../src/api/dependencies';

const object = (kind: DependencyObject['kind'], id: string, name: string): DependencyObject => ({ kind, id, name, context: kind === 'workflow' ? '/Produktion/Wartung' : 'fs-prod-01 · Produktion', enabled: true, revision: 'v1' });
const machine = object('machine', '11111111-1111-1111-1111-111111111111', 'FS-PROD-01');
const workflows = ['Dateien archivieren', 'Speicherplatz prüfen', 'Freigaben prüfen', 'Protokolle bereinigen', 'Nachtwartung', 'Wochenabschluss'].map((name, i) => object('workflow', `22222222-2222-2222-2222-22222222222${i}`, name));
const leg = (source: DependencyObject, target: DependencyObject, relation: string, label: string): DependencyLeg => ({ source, target, relation, locationCount: 1,
  locations: [{ sourceKind: source.kind, sourceId: source.id, nodeId: 'step-archive', label, field: relation === 'calls' ? 'data.config.workflowNameOrId' : 'data.targetMachineId', disabled: false, revision: 'v1', viaMachineId: null }] });
const direct = workflows.slice(0, 4).map((wf, i) => ({ object: wf, distance: 1, possible: false, paths: [{ links: [leg(wf, machine, 'usesMachine', ['Archiv kopieren', 'Kapazität prüfen', 'Freigaben lesen', 'Alte Logs entfernen'][i])] }] }));
const indirect = workflows.slice(4).map(wf => ({ object: wf, distance: 2, possible: false, paths: [{ links: [leg(wf, workflows[0], 'calls', 'Archivierung starten'), direct[0].paths[0].links[0]] }] }));
const report: DependencyReport = { object: machine, direction: 'usedBy', generatedAt: '2026-10-23T10:00:00Z', analysisRevision: 'snapshot1', complete: true, limitations: [], counts: { direct: 4, indirect: 2, possible: 0 }, items: [...direct, ...indirect], problems: [], nextCursor: null };
const entry = `/dependencies?kind=machine&id=${machine.id}&direction=usedBy`;

async function setup(page: Page) {
  await installDefaultMocks(page);
  await page.addInitScript(theme => {
    localStorage.setItem('nodepilot.lang', 'de');
    localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme }, version: 0 }));
  }, process.env.NP_DESIGN_SKIN ?? 'dark');
  await page.route('**/api/dependencies/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/objects')) return route.fulfill({ json: { items: [{ object: machine, usedByDirect: 4, usedByIndirect: 2, problems: 0 }], analysisRevision: 'snapshot1', nextCursor: null } });
    if (url.pathname.endsWith('/evidence')) {
      const item = report.items.find(i => i.object.id === url.searchParams.get('relatedId'))!;
      const locations = item.paths.flatMap(p => p.links.flatMap(l => l.locations));
      return route.fulfill({ json: { analysisRevision: 'snapshot1', paths: item.paths, items: locations, total: locations.length, nextCursor: null } });
    }
    return route.fulfill({ json: url.searchParams.get('direction') === 'uses' ? { ...report, direction: 'uses', items: [], counts: { direct: 0, indirect: 0, possible: 0 } } : report });
  });
}

test('search, explanations, designer deep link, map and browser history', async ({ page }) => {
  await setup(page);
  await page.goto('/dependencies');
  await page.getByRole('button', { name: /FS-PROD-01/ }).click();
  await expect(page.locator('.np-dependency-summary')).toContainText('4');
  await page.getByRole('button', { name: /Nachtwartung.*Details/ }).click();
  const details = page.getByRole('complementary', { name: 'Warum betroffen?' });
  await expect(details.getByText('»Nachtwartung« ruft auf »Dateien archivieren«. »Dateien archivieren« verwendet »FS-PROD-01«.')).toBeVisible();
  await expect(details.getByRole('link', { name: 'Schritt im Designer öffnen' }).first()).toHaveAttribute('href', /focusNode=step-archive/);
  await page.getByRole('button', { name: 'Karte', exact: true }).click();
  await expect(page.locator('.np-dependency-map-node')).toHaveCount(7);
  await page.goBack();
  await expect(page.getByRole('button', { name: 'Übersicht', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Verwendet', exact: true }).click();
  await expect(page.getByText('Keine statisch erkannten Beziehungen im sichtbaren Bereich.')).toBeVisible();
});

test('revocation removes cached relationships and evidence; refresh recovers', async ({ page }) => {
  await setup(page); await page.goto(`${entry}&related=workflow:${workflows[4].id}`);
  await expect(page.getByRole('complementary', { name: 'Warum betroffen?' })).toBeVisible();
  await page.route(`**/api/dependencies/machine/${machine.id}?**`, r => r.fulfill({ status: 404 }));
  await page.getByRole('button', { name: 'Aktualisieren', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('nicht mehr verfügbar');
  await expect(page.getByRole('complementary', { name: 'Warum betroffen?' })).toHaveCount(0);
  await expect(page.locator('.np-dependency-name')).toHaveCount(0);
});

test('changed evidence is hidden and incompleteness is explicit', async ({ page }) => {
  await setup(page);
  await page.route('**/api/dependencies/**/evidence?**', r => r.fulfill({ json: { analysisRevision: 'new', paths: [], items: direct[0].paths[0].links[0].locations, total: 1, nextCursor: null } }));
  await page.route(`**/api/dependencies/machine/${machine.id}?**`, r => r.fulfill({ json: { ...report, complete: false } }));
  await page.goto(`${entry}&related=workflow:${workflows[4].id}`);
  await expect(page.getByText(/Analyse begrenzt:/)).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Stand hat sich geändert');
  await expect(page.getByRole('link', { name: 'Schritt im Designer öffnen' })).toHaveCount(0);
});

test('desktop and mobile remain readable with keyboard access', async ({ page }) => {
  await setup(page); await page.setViewportSize({ width: 1536, height: 1024 });
  await page.goto(`${entry}&related=workflow:${workflows[4].id}`);
  await expect(page.getByRole('link', { name: 'Schritt im Designer öffnen' }).first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  if (process.env.NP_DESIGN_ROUND) {
    await page.screenshot({ path: `../../design/dependencies/compare/desktop-r${process.env.NP_DESIGN_ROUND}.png`, fullPage: true, animations: 'disabled' });
    const metrics = await page.evaluate(() => ['h1[aria-current="page"]', '.np-dependencies-header', '.np-dependencies-header p', '.np-dependency-search', '.np-dependency-selection', '.np-dependency-overview', '.np-dependency-details', '.np-dependency-section-title', '.np-dependency-row', '.np-dependency-name', '.np-dependency-explanation', '.np-dependency-scope'].map(selector => {
      const el = document.querySelector(selector)!; const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
      return { selector, x: r.x, y: r.y, width: r.width, height: r.height, fontSize: s.fontSize, lineHeight: s.lineHeight, color: s.color, background: s.backgroundColor, padding: s.padding, gap: s.gap, fontFamily: s.fontFamily };
    }));
    await writeFile(`../../design/dependencies/compare/metrics-r${process.env.NP_DESIGN_ROUND}.json`, JSON.stringify(metrics, null, 2));
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const close = page.getByRole('button', { name: 'Details schließen' });
  await close.focus(); await page.keyboard.press('Escape');
  await expect(page.getByRole('complementary', { name: 'Warum betroffen?' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Nachtwartung/ })).toBeFocused();
  if (process.env.NP_DESIGN_ROUND) {
    await page.locator('.np-dependencies-header').scrollIntoViewIfNeeded();
    await page.screenshot({ path: `../../design/dependencies/compare/mobile-r${process.env.NP_DESIGN_ROUND}.png`, fullPage: true, animations: 'disabled' });
    await page.getByRole('button', { name: /Nachtwartung/ }).click();
    const mobileDetails = page.getByRole('complementary', { name: 'Warum betroffen?' });
    await expect(mobileDetails).toBeVisible();
    await mobileDetails.locator('h2').scrollIntoViewIfNeeded();
    await page.screenshot({ path: `../../design/dependencies/compare/mobile-details-r${process.env.NP_DESIGN_ROUND}.png`, animations: 'disabled' });
    await page.setViewportSize({ width: 1536, height: 1024 });
    await page.goto('/dependencies');
    await expect(page.getByRole('button', { name: /FS-PROD-01/ })).toBeVisible();
    await page.screenshot({ path: `../../design/dependencies/compare/list-r${process.env.NP_DESIGN_ROUND}.png`, animations: 'disabled' });
    await page.goto(`${entry}&view=map&related=workflow:${workflows[4].id}`);
    await expect(page.locator('.np-dependency-map-node')).toHaveCount(7);
    await page.screenshot({ path: `../../design/dependencies/compare/map-r${process.env.NP_DESIGN_ROUND}.png`, animations: 'disabled' });
  }
});

test('designer evidence selects the saved step without acquiring an edit lock', async ({ page }) => {
  await setup(page);
  const workflow = workflows[0]; const writes: string[] = [];
  page.on('request', r => { if (r.method() !== 'GET' && r.url().includes('/api/workflows/')) writes.push(r.url()); });
  await page.route(`**/api/workflows/${workflow.id}`, r => r.fulfill({ json: { id: workflow.id, name: workflow.name, isEnabled: true, version: 1, checkedOutByUserId: null, definitionJson: JSON.stringify({ nodes: [{ id: 'step-archive', type: 'activity', position: { x: 100, y: 100 }, data: { activityType: 'runScript', label: 'Archiv kopieren', config: { script: 'Write-Output OK' } } }], edges: [] }) } }));
  await page.goto(`${entry}&related=workflow:${workflow.id}`);
  await page.getByRole('link', { name: 'Schritt im Designer öffnen' }).click();
  await expect(page.locator('.react-flow__node[data-id="step-archive"]')).toHaveClass(/selected/);
  expect(writes).toEqual([]);
});

for (const back of ['designer', 'browser'] as const) {
  test(`${back} back restores dependency selection, filters, map and scroll`, async ({ page }) => {
    await setup(page);
    const workflow = workflows[0];
    await page.route(`**/api/workflows/${workflow.id}`, r => r.fulfill({ json: {
      id: workflow.id, name: workflow.name, isEnabled: true, version: 1, checkedOutByUserId: null,
      definitionJson: JSON.stringify({ nodes: [{ id: 'step-archive', type: 'activity', position: { x: 100, y: 100 }, data: { activityType: 'runScript', label: 'Archiv kopieren', config: {} } }], edges: [] }),
    } }));
    const origin = `${entry}&q=FS-PROD&filter=machine&view=map&related=workflow:${workflow.id}`;
    await page.goto(origin);
    const viewport = page.locator('.np-dependency-map .react-flow__viewport');
    await expect(viewport).toBeVisible();
    const scale = () => viewport.evaluate(el => new DOMMatrix(getComputedStyle(el).transform).a);
    // The map fits its content on first open; wait until it moved away from React Flow's identity default.
    await expect(viewport).not.toHaveCSS('transform', 'matrix(1, 0, 0, 1, 0, 0)');
    const fitted = await scale();
    await page.locator('.np-dependency-map .react-flow__controls-zoomin').click();
    // Wait for React Flow's animated control movement to finish before recording it.
    await expect.poll(scale).toBeCloseTo(fitted * 1.2, 3);
    const transform = await viewport.evaluate(el => getComputedStyle(el).transform);
    const tech = page.locator('.np-dependency-tech').first();
    await tech.locator('summary').click();
    const source = page.getByRole('link', { name: 'Schritt im Designer öffnen' }).first();
    await source.scrollIntoViewIfNeeded();
    const scrollTop = await page.locator('#np-main-scroll').evaluate(el => el.scrollTop);
    await source.click();
    await expect(page.locator('.react-flow__node[data-id="step-archive"]')).toHaveClass(/selected/);
    if (back === 'designer') await page.getByRole('button', { name: 'Zurück zu Abhängigkeiten' }).click();
    else await page.goBack();
    await expect(page).toHaveURL(new URL(origin, page.url()).href);
    await expect(page.getByRole('textbox', { name: 'Objekt suchen' })).toHaveValue('FS-PROD');
    await expect(page.getByRole('button', { name: 'Maschinen', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('complementary', { name: 'Warum betroffen?' })).toBeVisible();
    await expect(tech).toHaveAttribute('open', '');
    await expect(viewport).toHaveCSS('transform', transform);
    await expect.poll(() => page.locator('#np-main-scroll').evaluate(el => el.scrollTop)).toBe(scrollTop);
  });
}

test('wide layout restores the scroll position inside the details panel', async ({ page }) => {
  await setup(page); await page.setViewportSize({ width: 1536, height: 700 });
  const workflow = workflows[0];
  await page.route(`**/api/workflows/${workflow.id}`, r => r.fulfill({ json: {
    id: workflow.id, name: workflow.name, isEnabled: true, version: 1, checkedOutByUserId: null,
    definitionJson: JSON.stringify({ nodes: [{ id: 'step-archive', type: 'activity', position: { x: 100, y: 100 }, data: { activityType: 'runScript', label: 'Archiv kopieren', config: {} } }], edges: [] }),
  } }));
  await page.goto(`${entry}&related=workflow:${workflows[4].id}`);
  const details = page.getByRole('complementary', { name: 'Warum betroffen?' });
  const links = details.getByRole('link', { name: 'Schritt im Designer öffnen' });
  await expect(links).toHaveCount(2);
  await expect(details.getByText('in »Dateien archivieren«')).toBeVisible();
  const panelScroll = await details.evaluate(el => { el.scrollTop = el.scrollHeight; return el.scrollTop; });
  expect(panelScroll).toBeGreaterThan(0);
  await links.last().click();
  await expect(page.locator('.react-flow__node[data-id="step-archive"]')).toHaveClass(/selected/);
  await page.goBack();
  await expect.poll(() => details.evaluate(el => el.scrollTop)).toBe(panelScroll);
});

test('object list searches while typing and filters by problems, missing use and usage', async ({ page }) => {
  await setup(page);
  const spare = object('credential', '33333333-3333-3333-3333-333333333333', 'Altes Dienstkonto');
  const requests: URL[] = [];
  page.on('request', r => { if (r.url().includes('/api/dependencies/objects')) requests.push(new URL(r.url())); });
  await page.route('**/api/dependencies/objects?**', route => {
    const state = new URL(route.request().url()).searchParams.get('state');
    const items = state === 'problems' ? [{ object: workflows[4], usedByDirect: 0, usedByIndirect: 0, problems: 2 }]
      : state === 'unused' ? [{ object: spare, usedByDirect: 0, usedByIndirect: 0, problems: 0 }]
        : [{ object: machine, usedByDirect: 4, usedByIndirect: 2, problems: 0 }];
    return route.fulfill({ json: { items, analysisRevision: 'snapshot1', nextCursor: null } });
  });
  await page.goto('/dependencies');
  await expect(page.getByRole('button', { name: /FS-PROD-01/ })).toContainText('4 direkt · 2 mittelbar');
  await page.getByRole('textbox', { name: 'Objekt suchen' }).fill('FS-PROD');
  await expect(page).toHaveURL(/q=FS-PROD/);
  await expect.poll(() => requests.some(u => u.searchParams.get('q') === 'FS-PROD')).toBe(true);
  await page.getByRole('button', { name: 'Mit Problemen', exact: true }).click();
  await expect(page.getByRole('button', { name: /Nachtwartung/ })).toContainText('2 Probleme');
  await page.getByRole('button', { name: 'Ohne erkannte Verwendung', exact: true }).click();
  await expect(page.getByText(/Workflows sind ausgenommen/)).toBeVisible();
  await expect(page.getByRole('button', { name: /Altes Dienstkonto/ })).toContainText('Keine erkannte Verwendung');
  await page.getByRole('button', { name: 'Verwendung', exact: true }).click();
  await expect.poll(() => requests.at(-1)?.searchParams.get('sort')).toBe('usage');
  expect(requests.at(-1)?.searchParams.get('state')).toBe('unused');
  expect(requests.at(-1)?.searchParams.get('q')).toBe('FS-PROD');
});

test('map shows both directions and expands further levels on demand', async ({ page }) => {
  await setup(page);
  const credential = object('credential', '33333333-3333-3333-3333-333333333334', 'Dienstkonto Archiv');
  const uses: DependencyReport = { ...report, direction: 'uses', counts: { direct: 1, indirect: 0, possible: 0 },
    items: [{ object: credential, distance: 1, possible: false, paths: [{ links: [leg(machine, credential, 'defaultCredential', 'FS-PROD-01')] }] }] };
  await page.route(`**/api/dependencies/machine/${machine.id}?**`, route =>
    route.fulfill({ json: new URL(route.request().url()).searchParams.get('direction') === 'uses' ? uses : report }));
  await page.route('**/api/dependencies/**/evidence?**', route =>
    route.fulfill({ json: { analysisRevision: 'snapshot1', paths: [], items: [], total: 0, nextCursor: null } }));
  await page.goto(`${entry}&view=map`);
  const nodes = page.locator('.np-dependency-map-node');
  await expect(nodes).toHaveCount(6);
  await page.getByRole('button', { name: '2 weitere über »Dateien archivieren« einblenden' }).click();
  await expect(nodes).toHaveCount(8);
  if (process.env.NP_DESIGN_ROUND) {
    await page.setViewportSize({ width: 1536, height: 1024 });
    await page.locator('.np-dependency-map .react-flow__controls-fitview').click();
    await page.screenshot({ path: `../../design/dependencies/compare/map-both-r${process.env.NP_DESIGN_ROUND}.png`, animations: 'disabled' });
  }
  await page.getByRole('button', { name: 'Weitere Ebenen unter »Dateien archivieren« ausblenden' }).click();
  await expect(nodes).toHaveCount(6);
  await nodes.filter({ hasText: 'Dienstkonto Archiv' }).getByRole('button').first().click();
  await expect(page).toHaveURL(/direction=uses/);
  await expect(page).toHaveURL(new RegExp(`related=credential(:|%3A)${credential.id}`));
  await expect(page.getByRole('complementary', { name: 'Warum betroffen?' })).toBeVisible();
});
