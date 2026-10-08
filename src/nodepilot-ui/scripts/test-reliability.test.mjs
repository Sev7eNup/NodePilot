import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { collect, summarize, markdown, includeMissing } from './test-reliability.mjs';

const metadata = { suite: 'ui', shard: '1', runId: '10', runAttempt: 1, commit: 'abc' };
function report(status, statuses, expectedStatus = 'passed') {
  return { errors: [], suites: [{ title: 'editor.spec.ts', suites: [{ title: 'Editor', specs: [{
    file: 'editor.spec.ts', title: 'renders', tests: [{ projectName: 'chromium', status, expectedStatus,
      results: statuses.map((status, retry) => ({ status, retry, stdout: ['secret'], error: { message: 'secret' } })) }],
  }] }] }] };
}

test('records recovered failures without retaining sensitive runner output', () => {
  const record = collect(report('flaky', ['failed', 'passed']), metadata);
  assert.equal(JSON.stringify(record).includes('secret'), false);
  const result = summarize([record]);
  assert.equal(result.executed, 1);
  assert.equal(result.firstAttemptFailures, 1);
  assert.equal(result.retried, 1);
  assert.equal(result.retries, 1);
  assert.equal(result.flaky, 1);
  assert.equal(result.failed, 0);
});

test('expected failures and skipped cases do not inflate first-attempt failures', () => {
  const expected = collect(report('expected', ['failed'], 'failed'), metadata);
  const skipped = collect(report('skipped', ['skipped']), { ...metadata, shard: '2' });
  const result = summarize([expected, skipped]);
  assert.equal(result.executed, 1);
  assert.equal(result.skipped, 1);
  assert.equal(result.firstAttemptFailures, 0);
});

test('counts terminal failures and all retries', () => {
  const result = summarize([collect(report('unexpected', ['timedOut', 'failed', 'failed']), metadata)]);
  assert.equal(result.retries, 2);
  assert.equal(result.failed, 1);
  assert.equal(result.flaky, 0);
});

test('rerun replaces one shard without removing other shards or counting recurrence twice', () => {
  const flaky = collect(report('flaky', ['failed', 'passed']), metadata);
  const rerun = collect(report('expected', ['passed']), { ...metadata, runAttempt: 2 });
  const otherShard = collect(report('flaky', ['failed', 'passed']), { ...metadata, shard: '2' });
  const nextRun = collect(report('flaky', ['failed', 'passed']), { ...metadata, runId: '11' });
  const result = summarize([flaky, otherShard, rerun, nextRun, nextRun]);
  assert.equal(result.executed, 3);
  assert.equal(result.runs, 2);
  assert.equal(result.tests[0].flakyRuns, 2);
});

test('identical titles in demo and UI are distinct and missing reports are visible', () => {
  const ui = collect(report('expected', ['passed']), metadata);
  const demo = collect(report('expected', ['passed']), { ...metadata, suite: 'demo' });
  const missing = collect(null, { ...metadata, shard: '3' });
  const result = summarize([ui, demo, missing]);
  assert.equal(result.tests.length, 2);
  assert.equal(result.missing, 1);
  assert.match(markdown([missing], []), /N\/A/);
  assert.match(markdown([ui], []), /0 available main runs/);
});

test('recurrence requires different runs and Markdown escapes test titles', () => {
  const one = collect(report('flaky', ['failed', 'passed']), metadata);
  one.tests[0].title = '<script>|`test`';
  const two = { ...one, runId: '11' };
  const rendered = markdown([one], [one, two]);
  assert.match(rendered, /2 \/ 2/);
  assert.equal(rendered.includes('<script>'), false);
  assert.match(rendered, /&#124;/);
});

test('an absent shard artifact is represented as missing rather than a smaller green suite', () => {
  const records = includeMissing([collect(report('expected', ['passed']), metadata)],
    [['ui', '1'], ['ui', '2'], ['demo', '1']], metadata);
  assert.equal(records.length, 3);
  assert.equal(summarize(records).missing, 2);
  assert.equal(summarize(records).executed, 1);
});

test('consumes real Playwright results for pass, retry, expected failure, skip and terminal failure', () => {
  const root = resolve(tmpdir());
  const probe = mkdtempSync(join(root, 'nodepilot-reliability-'));
  try {
    const output = join(probe, 'report.json');
    writeFileSync(join(probe, 'playwright.config.mjs'), "export default { testDir: '.', testMatch: 'probe.spec.mjs', retries: 1, workers: 1, reporter: 'json', outputDir: './results' };\n");
    writeFileSync(join(probe, 'probe.spec.mjs'), `import { test, expect } from ${JSON.stringify(import.meta.resolve('@playwright/test'))};
test('passes', () => expect(true).toBe(true));
test('recovers', ({}, info) => expect(info.retry).toBe(1));
test('expected failure', () => { test.fail(); expect(true).toBe(false); });
test.skip('skipped', () => {});
test('fails', () => expect(true).toBe(false));
`);
    const run = spawnSync(process.execPath, [resolve('node_modules/@playwright/test/cli.js'), 'test', '--config', join(probe, 'playwright.config.mjs')], {
      env: { ...process.env, PLAYWRIGHT_JSON_OUTPUT_FILE: output }, encoding: 'utf8', timeout: 60_000,
    });
    assert.equal(run.status, 1, run.stderr);
    const result = summarize([collect(JSON.parse(readFileSync(output, 'utf8')), metadata)]);
    assert.equal(result.executed, 4);
    assert.equal(result.skipped, 1);
    assert.equal(result.firstAttemptFailures, 2);
    assert.equal(result.flaky, 1);
    assert.equal(result.failed, 1);
    assert.equal(result.retries, 2);
  } finally {
    assert.equal(dirname(probe), root);
    rmSync(probe, { recursive: true, force: true });
  }
});
