import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import VitestRetryReporter, { annotation, summary } from './vitest-retry-reporter.mjs';

function testCase(workspace, name, state, diagnostic) {
  return {
    fullName: name,
    module: { moduleId: join(workspace, 'src', 'nodepilot-ui', 'src', 'a.test.ts') },
    result: () => ({ state }),
    diagnostic: () => diagnostic,
  };
}

test('annotation escapes workflow-command characters and counts retries', () => {
  assert.equal(
    annotation({ file: 'src/a,b:c.test.ts', name: 'shows 100%\nthen hides', retries: 1 }),
    '::warning file=src/a%2Cb%3Ac.test.ts,title=Flaky Vitest test::shows 100%25%0Athen hides passed only after 1 retry');
  assert.match(annotation({ file: 'a.test.ts', name: 'x', retries: 2 }), /after 2 retries$/);
});

test('summary lists every retried test and says so when there is none', () => {
  const text = summary([{ file: 'src/a.test.ts', name: 'Editor > a|b', retries: 2 }]);
  assert.match(text, /1 test\(s\) passed only after a retry\./);
  assert.match(text, /\| src\/a\.test\.ts \| Editor &gt; a&#124;b \| 2 \|/);
  assert.match(summary([]), /No test needed a retry\./);
});

test('reporter keeps only passes that needed a retry and publishes them on run end', (t) => {
  const workspace = mkdtempSync(join(tmpdir(), 'vitest-retry-'));
  const summaryFile = join(workspace, 'summary.md');
  const env = { workspace: process.env.GITHUB_WORKSPACE, summary: process.env.GITHUB_STEP_SUMMARY };
  process.env.GITHUB_WORKSPACE = workspace;
  process.env.GITHUB_STEP_SUMMARY = summaryFile;
  const log = mock.method(console, 'log', () => {});
  t.after(() => {
    log.mock.restore();
    for (const [key, value] of [['GITHUB_WORKSPACE', env.workspace], ['GITHUB_STEP_SUMMARY', env.summary]]) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
    rmSync(workspace, { recursive: true, force: true });
  });

  const reporter = new VitestRetryReporter();
  reporter.onTestCaseResult(testCase(workspace, 'recovered', 'passed', { flaky: true, retryCount: 1 }));
  reporter.onTestCaseResult(testCase(workspace, 'stable', 'passed', { flaky: false, retryCount: 0 }));
  reporter.onTestCaseResult(testCase(workspace, 'broken', 'failed', { flaky: false, retryCount: 2 }));
  reporter.onTestCaseResult(testCase(workspace, 'not run', 'skipped', undefined));
  reporter.onTestRunEnd();

  assert.deepEqual(log.mock.calls.map(call => call.arguments[0]), [
    '::warning file=src/nodepilot-ui/src/a.test.ts,title=Flaky Vitest test::recovered passed only after 1 retry',
  ]);
  const written = readFileSync(summaryFile, 'utf8');
  assert.match(written, /\| src\/nodepilot-ui\/src\/a\.test\.ts \| recovered \| 1 \|/);
  assert.doesNotMatch(written, /stable|broken|not run/);
});
