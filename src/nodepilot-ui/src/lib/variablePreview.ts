import i18n from '../i18n';
import type { StepExecution } from '../types/api';
import { parseOutputParametersJson } from './outputParameters';

/** Maximum characters shown in a hover preview before truncation. Keeps tooltips readable. */
export const PREVIEW_MAX_CHARS = 300;

export interface VariablePreview {
  /** Where the value came from: "stdout", "stderr", or "param" (for `{{x.param.foo}}`). */
  channel: 'stdout' | 'stderr' | 'param' | 'unknown';
  /** Resolved string value, already truncated to PREVIEW_MAX_CHARS. Empty string if no value. */
  value: string;
  /** True when the underlying value was longer than PREVIEW_MAX_CHARS and got cut. */
  truncated: boolean;
  /** Human-readable label for the source (e.g. "stdout (last run)" / "param: hostname"). */
  sourceLabel: string;
}

/**
 * Resolves the last-run value of a `{{step.X}}` expression from the StepExecution of the
 * producing step. `.output` and `.error` map to direct fields; `.param.X` reads the structured
 * output parameters and falls back to scanning stdout.
 *
 * Returns null when there is no value to show (no past run, empty channel).
 */
export function resolveVariablePreview(step: StepExecution | undefined, expression: string): VariablePreview | null {
  if (!step) return null;
  // Strip outer braces if present and split on '.', the same shape as the engine's resolver.
  const inner = expression.replaceAll(/^\{\{|\}\}$/g, '');
  const parts = inner.split('.');
  // parts[0] is the alias (varName / stepId). parts[1..] is the field path.
  const tail = parts.slice(1).join('.');

  if (tail === '' || tail === 'output') {
    const raw = step.output ?? '';
    if (!raw) return null;
    return preview('stdout', raw, i18n.t('properties:variablePreview.stdoutLastRun'));
  }
  // Only `.error` — the engine grammar has no `errorOutput` tail, and previewing one made an
  // unresolvable reference look live.
  if (tail === 'error') {
    const raw = step.errorOutput ?? '';
    if (!raw) return null;
    return preview('stderr', raw, i18n.t('properties:variablePreview.stderrLastRun'));
  }
  if (tail.startsWith('param.')) {
    const paramName = tail.slice('param.'.length);
    // Primary source: the structured OutputParameters dict persisted alongside the step and
    // emitted by ExecutionsController.GetSteps on `outputParametersJson`. It matches what the
    // engine substitutes at run time. Malformed JSON or an unknown param falls through to the
    // stdout scan below, because a hover preview must never throw.
    const paramMap = parseOutputParametersJson(step.outputParametersJson);
    if (paramMap && Object.prototype.hasOwnProperty.call(paramMap, paramName)) {
      return preview('param', paramMap[paramName], i18n.t('properties:variablePreview.paramLastRun', { name: paramName }));
    }
    // Fallback for steps that carry no structured snapshot: scan stdout for a
    // "$paramName = value" or "paramName: value" line so the tooltip still shows
    // something useful instead of going blank.
    const stdout = step.output ?? '';
    if (!stdout) return null;
    const extracted = extractParamFromOutput(stdout, paramName);
    if (extracted !== null) {
      return preview('param', extracted, i18n.t('properties:variablePreview.paramStdoutScan', { name: paramName }));
    }
    return preview('param', stdout, i18n.t('properties:variablePreview.paramFullStdout', { name: paramName }));
  }
  // Unknown suffix: the meaning is not known, but stdout is still worth showing.
  const raw = step.output ?? '';
  if (!raw) return null;
  return preview('unknown', raw, i18n.t('properties:variablePreview.unknownSuffix', { tail }));
}

function preview(channel: VariablePreview['channel'], raw: string, sourceLabel: string): VariablePreview {
  const truncated = raw.length > PREVIEW_MAX_CHARS;
  const value = truncated ? raw.slice(0, PREVIEW_MAX_CHARS) : raw;
  return { channel, value, truncated, sourceLabel };
}

/**
 * Extracts a single param value from a runScript-style stdout capture. The engine appends a
 * `$paramName = value` line per declared variable; a plain `paramName: value` colon form is
 * matched as well. Returns null when neither pattern matches, leaving the caller to decide
 * (typically: show the full stdout instead).
 */
function extractParamFromOutput(stdout: string, paramName: string): string | null {
  const escaped = paramName.replaceAll(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const dollarForm = new RegExp(`^\\s*\\$${escaped}\\s*=\\s*(.+)$`, 'm');
  const colonForm = new RegExp(`^\\s*${escaped}\\s*:\\s*(.+)$`, 'm');
  const m1 = dollarForm.exec(stdout);
  if (m1) return m1[1].trim();
  const m2 = colonForm.exec(stdout);
  if (m2) return m2[1].trim();
  return null;
}
