import { appendFileSync } from 'node:fs';
import { relative } from 'node:path';
import { cell } from './test-reliability.mjs';

// Vitest counts a test that passed on a retry as a plain pass. On CI this reporter turns each
// such test into a warning annotation and lists it in the job summary, so a flake stays visible.

function escapeData(value) {
  return String(value).replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A');
}

function escapeProperty(value) {
  return escapeData(value).replaceAll(':', '%3A').replaceAll(',', '%2C');
}

export function annotation({ file, name, retries }) {
  const message = `${name} passed only after ${retries} ${retries === 1 ? 'retry' : 'retries'}`;
  return `::warning file=${escapeProperty(file)},title=Flaky Vitest test::${escapeData(message)}`;
}

export function summary(rows) {
  const lines = ['## Vitest retries', ''];
  if (!rows.length) return [...lines, 'No test needed a retry.', ''].join('\n');
  lines.push(`${rows.length} test(s) passed only after a retry.`, '', '| File | Test | Retries |', '|---|---|---:|');
  for (const row of rows) lines.push(`| ${cell(row.file)} | ${cell(row.name)} | ${row.retries} |`);
  return [...lines, ''].join('\n');
}

export default class VitestRetryReporter {
  #rows = [];

  onTestCaseResult(testCase) {
    const diagnostic = testCase.diagnostic();
    if (testCase.result().state !== 'passed' || !diagnostic?.flaky) return;
    this.#rows.push({
      file: relative(process.env.GITHUB_WORKSPACE ?? process.cwd(), testCase.module.moduleId).replaceAll('\\', '/'),
      name: testCase.fullName,
      retries: diagnostic.retryCount,
    });
  }

  onTestRunEnd() {
    for (const row of this.#rows) console.log(annotation(row));
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary(this.#rows));
  }
}
