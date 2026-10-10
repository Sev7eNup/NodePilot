import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

// Keep history free of error messages, source snippets, stdout and attachments.
export function collect(report, metadata) {
  const tests = [];
  function visit(suites, parents = []) {
    for (const suite of suites) {
      const titles = [...parents, suite.title];
      for (const spec of suite.specs ?? []) {
        for (const test of spec.tests) {
          const attempts = test.results.map(result => ({ status: result.status, retry: result.retry }));
          tests.push({
            file: spec.file.replaceAll('\\', '/'), title: [...titles, spec.title].join(' > '),
            project: test.projectName, outcome: test.status, expectedStatus: test.expectedStatus,
            attempts,
          });
        }
      }
      visit(suite.suites ?? [], titles);
    }
  }
  if (report) visit(report.suites);
  return { schemaVersion: 1, ...metadata, missing: !report, runnerErrors: report?.errors?.length ?? 0, tests };
}

export function summarize(records) {
  // A rerun replaces the same run/shard, rather than inflating recurrence.
  const latest = new Map();
  for (const record of records) {
    if (record.schemaVersion !== 1 || !Array.isArray(record.tests)) throw new Error('Unsupported reliability report');
    const key = JSON.stringify([record.runId, record.suite, record.shard]);
    if (!latest.has(key) || record.runAttempt > latest.get(key).runAttempt) latest.set(key, record);
  }
  const totals = { executed: 0, skipped: 0, firstAttemptFailures: 0, retried: 0, retries: 0, flaky: 0, failed: 0, missing: 0, runnerErrors: 0 };
  const byTest = new Map();
  const runs = new Set();
  for (const record of latest.values()) {
    runs.add(record.runId);
    totals.missing += Number(record.missing);
    totals.runnerErrors += record.runnerErrors;
    for (const test of record.tests) {
      const executed = test.attempts.some(a => a.status !== 'skipped');
      if (!executed) { totals.skipped++; continue; }
      totals.executed++;
      if (test.attempts[0].status !== test.expectedStatus) totals.firstAttemptFailures++;
      const retries = test.attempts.filter(a => a.retry > 0).length;
      totals.retries += retries;
      totals.retried += Number(retries > 0);
      totals.flaky += Number(test.outcome === 'flaky');
      totals.failed += Number(test.outcome === 'unexpected');
      const key = JSON.stringify([record.suite, test.project, test.file, test.title]);
      const row = byTest.get(key) ?? { suite: record.suite, project: test.project, file: test.file, title: test.title, runs: new Set(), flakyRuns: new Set(), failedRuns: new Set(), retries: 0 };
      row.runs.add(record.runId);
      if (test.outcome === 'flaky') row.flakyRuns.add(record.runId);
      if (test.outcome === 'unexpected') row.failedRuns.add(record.runId);
      row.retries += retries;
      byTest.set(key, row);
    }
  }
  return { ...totals, runs: runs.size, tests: [...byTest.values()]
    .map(row => ({ ...row, runs: row.runs.size, flakyRuns: row.flakyRuns.size, failedRuns: row.failedRuns.size }))
    .sort((a, b) => b.flakyRuns - a.flakyRuns || b.retries - a.retries || a.title.localeCompare(b.title)) };
}

export function includeMissing(records, expected, metadata) {
  return [...records, ...expected.filter(([suite, shard]) => !records.some(r => r.suite === suite && r.shard === shard))
    .map(([suite, shard]) => collect(null, { ...metadata, suite, shard }))];
}

function cell(value) {
  return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('|', '&#124;').replaceAll('`', '&#96;').replace(/[\r\n]/g, ' ');
}

export function markdown(current, history) {
  const now = summarize(current);
  const past = summarize(history);
  const percent = value => now.executed ? `${(100 * value / now.executed).toFixed(1)}%` : 'N/A';
  const lines = [
    '## Playwright reliability', '',
    'UI and browser-demo tests use mocked APIs or an in-memory backend. These results do not establish server, database or WinRM reliability.', '',
    '| Current run | Count |', '|---|---:|',
    `| Executed test cases | ${now.executed} |`, `| Skipped / not executed | ${now.skipped} |`,
    `| Unexpected first attempts | ${now.firstAttemptFailures} (${percent(now.firstAttemptFailures)}) |`,
    `| Cases retried / additional attempts | ${now.retried} / ${now.retries} |`,
    `| Passed after retry (flaky) | ${now.flaky} (${percent(now.flaky)}) |`,
    `| Finally unexpected | ${now.failed} |`, `| Runner errors / missing reports | ${now.runnerErrors} / ${now.missing} |`, '',
    'Expected failures are judged against their declared expected status. Missing reports are never counted as passing tests.', '',
    `Historical baseline: ${past.runs} available main runs (latest completed runs with retained artifacts; missing history is not zero flakes).`, '',
    '### Current retries and failures', '',
    '| Suite / project | Test | Retries | Flaky | Failed |', '|---|---|---:|---:|---:|',
  ];
  const currentRows = now.tests.filter(t => t.retries || t.failedRuns);
  for (const row of currentRows.slice(0, 50)) lines.push(`| ${cell(row.suite)} / ${cell(row.project)} | ${cell(row.title)} | ${row.retries} | ${row.flakyRuns} | ${row.failedRuns} |`);
  if (!currentRows.length) lines.push('| — | No retried or failed test cases recorded | 0 | 0 | 0 |');
  lines.push('', '### Recurring flakes in main history', '', '| Suite / project | Test | Flaky runs / observed runs |', '|---|---|---:|');
  const recurring = past.tests.filter(t => t.flakyRuns >= 2);
  for (const row of recurring.slice(0, 50)) lines.push(`| ${cell(row.suite)} / ${cell(row.project)} | ${cell(row.title)} | ${row.flakyRuns} / ${row.runs} |`);
  if (!recurring.length) lines.push('| — | No recurrence established in available history | — |');
  lines.push('', `Tables show at most 50 entries (${currentRows.length} current, ${recurring.length} recurring). Full test records are in the e2e-reliability artifact.`, '');
  return lines.join('\n');
}

function readReports(directory) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? readReports(path) : entry.name.endsWith('.json') ? [JSON.parse(readFileSync(path, 'utf8'))] : [];
  });
}

function main([command, ...args]) {
  if (command === 'collect' && args.length === 4) {
    const [input, output, suite, shard] = args;
    const record = collect(existsSync(input) ? JSON.parse(readFileSync(input, 'utf8')) : null, {
      suite, shard, runId: process.env.GITHUB_RUN_ID ?? 'local', runAttempt: Number(process.env.GITHUB_RUN_ATTEMPT ?? 1),
      commit: process.env.GITHUB_SHA ?? 'local',
    });
    mkdirSync(dirname(output), { recursive: true });
    writeFileSync(output, JSON.stringify(record, null, 2) + '\n');
    if (record.missing) console.warn(`Missing Playwright report: ${input}`);
  } else if (command === 'report' && args.length === 3) {
    const [currentDirectory, historyDirectory, output] = args;
    const expected = (process.env.EXPECTED_RELIABILITY_SHARDS ?? '').split(',').filter(Boolean).map(pair => pair.split(':'));
    const current = includeMissing(readReports(currentDirectory), expected, {
      runId: process.env.GITHUB_RUN_ID ?? 'local', runAttempt: Number(process.env.GITHUB_RUN_ATTEMPT ?? 1),
      commit: process.env.GITHUB_SHA ?? 'local',
    });
    if (!current.length) throw new Error('No current reliability reports found');
    for (const record of current.filter(r => r.missing)) {
      mkdirSync(currentDirectory, { recursive: true });
      writeFileSync(join(currentDirectory, `missing-${record.suite}-${record.shard}.json`), JSON.stringify(record, null, 2) + '\n');
    }
    const history = readReports(historyDirectory).filter(r => !current.some(c => c.runId === r.runId));
    const result = markdown(current, history);
    mkdirSync(dirname(output), { recursive: true });
    writeFileSync(output, result);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, result);
    console.log(result);
  } else throw new Error('Usage: test-reliability.mjs collect <input.json> <output.json> <suite> <shard> | report <current-dir> <history-dir> <output.md>');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main(process.argv.slice(2));
