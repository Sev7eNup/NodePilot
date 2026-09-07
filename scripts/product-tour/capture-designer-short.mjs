import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDefaultMocks } from '../../src/nodepilot-ui/e2e/fixtures/mockApi.ts';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'src/nodepilot-ui/package.json'));
const { chromium } = require('playwright');
const out = path.join(root, 'out/nodepilot-designer-short-8s/assets');
const base = 'http://localhost:5173';
const id = '80808080-8080-8080-8080-808080808080';
const user = { id: '00000000-0000-0000-0000-000000000099', username: 'demo-admin', role: 'Admin' };
const node = (id, label, activityType, x, y, config = {}, extra = {}) => ({
  id, type: 'activity', position: { x, y }, data: { label, activityType, config, ...extra },
});
const definition = {
  nodes: [
    node('schedule', 'Every morning', 'scheduleTrigger', 340, 0, { cronExpression: '0 7 * * 1-5', timeZoneId: 'UTC' }),
    node('copy', 'File Copy', 'fileOperation', 140, 180, { operation: 'copy', path: 'C:\\Ops\\daily.log', destination: 'D:\\Archive\\daily.log' }, { targetMachineId: 'demo-machine', outputVariable: 'archive' }),
    node('script', 'PowerShell', 'runScript', 540, 180, { script: "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |\n    Select-Object DeviceID, FreeSpace, Size | ConvertTo-Json" }, { targetMachineId: 'demo-machine', outputVariable: 'disk' }),
    node('join', 'Wait for both', 'junction', 340, 360, { mode: 'waitAll' }),
    node('llm', 'LLM Summary', 'llmQuery', 340, 540, { prompt: 'Create a concise operations brief. Disk capacity: {{disk.output}}. File archive: {{archive.output}}.', systemPrompt: 'Summarize the supplied results. Highlight any issue that needs attention.', temperature: 0.2, maxTokens: 512 }, { outputVariable: 'brief' }),
    node('return', 'Return report', 'returnData', 340, 720, { data: { report: '{{brief.output}}' } }),
  ],
  edges: [['schedule', 'copy'], ['schedule', 'script'], ['copy', 'join'], ['script', 'join'], ['join', 'llm'], ['llm', 'return']].map(([source, target], i) => ({
    id: `route-${i}`, source, target, type: 'labeled', sourceHandle: 'bottom', targetHandle: 'top', data: { condition: `${source}.success`, label: 'On Success' },
  })),
};
const date = '2026-09-07T10:00:00Z';
const workflow = {
  id, name: 'Daily Ops Brief', description: 'Archive a log and inspect disk capacity in parallel. Combine the results in an AI operations brief.',
  definitionJson: JSON.stringify(definition), version: 1, isEnabled: false,
  createdAt: date, updatedAt: date, createdBy: user.username, updatedBy: user.username,
  activityCount: 6, triggerTypes: ['scheduleTrigger'], successCount: 0, totalCount: 0, avgDurationMs: 0,
  folderId: '00000000-0000-0000-0000-000000000002', folderPath: '/',
  checkedOutByUserId: user.id, checkedOutByUserName: user.username, checkedOutAt: date,
  capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true }, lastExecution: null,
};
await mkdir(out, { recursive: true });
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 2080 }, deviceScaleFactor: 2, colorScheme: 'dark', locale: 'en-US', serviceWorkers: 'block' });
const page = await context.newPage();
const errors = [], requests = [];
page.on('pageerror', e => errors.push(e.message));
await context.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort('blockedbyclient'));
await installDefaultMocks(page);
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const req = route.request();
  requests.push({ method: req.method(), path: new URL(req.url()).pathname });
  if (req.method() !== 'GET') throw new Error('Read-only capture: unexpected mutation ' + req.method());
  const p = new URL(req.url()).pathname;
  let body;
  if (p === '/api/auth/me') body = user;
  else if (p === '/api/workflows') body = [workflow];
  else if (p === `/api/workflows/${id}`) body = workflow;
  else if (p === `/api/workflows/${id}/step-health` || p === `/api/workflows/${id}/step-stats`) body = {};
  else if (p === '/api/system/host-info') body = { machineName: 'NODEPILOT-DEMO', fqdn: 'nodepilot.example.test', domain: 'example.test', appVersion: '1.2.27' };
  else if (p === '/api/machines') body = [{ id: 'demo-machine', name: 'OPS-DEMO-01', hostname: 'ops.example.test', winRmPort: 5986, useSsl: true, defaultCredentialId: null, tags: 'demo,windows', isReachable: true, lastConnectivityCheck: date, usedByWorkflowCount: 1, recentStepCount: 0, recentFailedStepCount: 0, activeRunCount: 0 }];
  else if (p === '/api/ai/knowledge/capabilities') body = { enabled: true, llm: true, docs: true, operational: true, sourceCode: false, db: true };
  else return route.fallback();
  return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
});
await page.addInitScript(() => {
  localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark' }, version: 0 }));
  localStorage.setItem('nodepilot.lang.store', JSON.stringify({ state: { lang: 'en' }, version: 0 }));
  localStorage.setItem('i18nextLng', 'en');
  localStorage.setItem('nodepilot-design', JSON.stringify({ state: { designerMode: 'expert', toolbarLayout: 'compact', nodeStyle: 'classic', nodeScaleIndex: 4, labelFontOffsetIndex: 2, edgesAnimated: true, premiumCanvas: true }, version: 4 }));
});
try {
  await page.goto(`${base}/workflows/${id}`);
  await page.locator('.react-flow__node[data-id="llm"]').waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.getByRole('button', { name: 'Collapse execution panel', exact: true }).click();
  await page.locator('.react-flow__controls-fitview').click();
  await page.waitForTimeout(900);
  await page.screenshot({ path: path.join(out, 'designer-context.png') });
  await page.keyboard.press('F11');
  await page.locator('.react-flow__controls-fitview').click();
  await page.waitForTimeout(900);
  // A real native canvas capture; no changes to app styles or node renderers.
  const canvas = page.locator('.react-flow').first();
  await canvas.screenshot({ path: path.join(out, 'designer-canvas.png') });
  const geometry = await canvas.evaluate(el => {
    const origin = el.getBoundingClientRect();
    const nodes = [...el.querySelectorAll('.react-flow__node')].map(n => {
      const r = n.getBoundingClientRect();
      return { id: n.getAttribute('data-id'), x: r.x - origin.x, y: r.y - origin.y, width: r.width, height: r.height, text: n.textContent };
    });
    const edges = [...el.querySelectorAll('.react-flow__edge')].map(g => {
      const p = g.querySelector('.react-flow__edge-path');
      if (!p) return null;
      const m = p.getScreenCTM(), length = p.getTotalLength();
      return { id: g.getAttribute('data-id'), points: Array.from({ length: 121 }, (_, i) => {
        const a = p.getPointAtLength(length * i / 120), b = new DOMPoint(a.x, a.y).matrixTransform(m);
        return [b.x - origin.x, b.y - origin.y];
      }) };
    }).filter(Boolean);
    return { width: origin.width, height: origin.height, deviceScaleFactor: window.devicePixelRatio, nodes, edges };
  });
  await writeFile(path.join(out, 'designer-geometry.json'), JSON.stringify(geometry, null, 2));
  await writeFile(path.join(out, 'demo-workflow.json'), JSON.stringify(workflow, null, 2));
  await writeFile(path.join(out, 'capture-check.json'), JSON.stringify({ source: 'Fresh native NodePilot frontend capture; fully mocked read-only API', errors, requests, nodes: geometry.nodes.length, edges: geometry.edges.length }, null, 2));
  if (errors.length || geometry.nodes.length !== 6 || geometry.edges.length !== 6) throw new Error('Designer capture failed verification: ' + JSON.stringify(errors));
  console.log(JSON.stringify({ out, width: geometry.width, height: geometry.height, nodes: geometry.nodes, edges: geometry.edges.length }, null, 2));
} finally { await browser.close(); }
