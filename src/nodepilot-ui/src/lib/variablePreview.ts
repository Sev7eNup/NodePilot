import i18n from '../i18n';
import type { StepExecution } from '../types/api';
import { parseOutputParametersJson } from './outputParameters';

/** Maximum chars surfaced in the hover-preview before "…(truncated)". Keeps the tooltip readable. */
export const PREVIEW_MAX_CHARS = 300;

export interface VariablePreview {
  /** Where the value came from — "stdout", "stderr", or "param" (for `{{x.param.foo}}`). */
  channel: 'stdout' | 'stderr' | 'param' | 'unknown';
  /** Resolved string value, already truncated to PREVIEW_MAX_CHARS. Empty string if no value. */
  value: string;
  /** True when the underlying value was longer than PREVIEW_MAX_CHARS and got cut. */
  truncated: boolean;
  /** Human-readable label for the source (e.g. "stdout (last run)" / "param: hostname"). */
  sourceLabel: string;
}

/**
 * Resolves what the last-run value of a `{{step.X}}` expression was, given the StepExecution
 * for the producing step. Best-effort: `.output`/`.error` are direct fields; `.param.X` reads
 * from `step.output` because StepExecution doesn't expose structured outputParameters via the
 * /executions/{id}/steps endpoint — we surface the full stdout instead so the user can at
 * least see the raw value rather than an empty tooltip.
 *
 * Returns null when there's no value to show (no past run, empty channel).
 */
export function resolveVariablePreview(step: StepExecution | undefined, expression: string): VariablePreview | null {
  if (!step) return null;
  // Strip outer braces if present and split on '.' — same shape as the engine's resolver.
  const inner = expression.replaceAll(/^\{\{|\}\}$/g, '');
  const parts = inner.split('.');
  // parts[0] is the alias (varName / stepId). parts[1..] is the field path.
  const tail = parts.slice(1).join('.');

  if (tail === '' || tail === 'output') {
    const raw = step.output ?? '';
    if (!raw) return null;
    return preview('stdout', raw, i18n.t('properties:variablePreview.stdoutLastRun'));
  }
  if (tail === 'error' || tail === 'errorOutput') {
    const raw = step.errorOutput ?? '';
    if (!raw) return null;
    return preview('stderr', raw, i18n.t('properties:variablePreview.stderrLastRun'));
  }
  if (tail.startsWith('param.')) {
    const paramName = tail.slice('param.'.length);
    // Primary source: the structured OutputParameters dict persisted alongside the step.
    // ExecutionsController.GetSteps emits it on `outputParametersJson`; rebuilding the
    // databus from here matches what the engine itself would substitute at run time.
    // Malformed/empty JSON or unknown param → silently fall through to the stdout-scan
    // heuristic. Hovering a variable preview must never throw — worst case the user sees
    // the legacy best-effort view instead of a blank tooltip.
    const paramMap = parseOutputParametersJson(step.outputParametersJson);
    if (paramMap && Object.prototype.hasOwnProperty.call(paramMap, paramName)) {
      return preview('param', paramMap[paramName], i18n.t('properties:variablePreview.paramLastRun', { name: paramName }));
    }
    // Heuristic fallback: pre-fix runs (or step types that never produced
    // OutputParameters) don't carry the structured snapshot. Scan stdout for a
    // "$paramName = value" / "paramName: value" line so the tooltip still shows
    // something useful instead of going blank.
    const stdout = step.output ?? '';
    if (!stdout) return null;
    const extracted = extractParamFromOutput(stdout, paramName);
    if (extracted !== null) {
      return preview('param', extracted, i18n.t('properties:variablePreview.paramStdoutScan', { name: paramName }));
    }
    return preview('param', stdout, i18n.t('properties:variablePreview.paramFullStdout', { name: paramName }));
  }
  // Unknown suffix — we don't lie about what it means, but we can still show the stdout.
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
 * Tries to pluck a single param value out of a runScript-style stdout capture. The engine
 * appends a block of `$paramName = value` lines for each declared variable; we look for
 * either that pattern or a standard `paramName: value` colon-form. Returns null if neither
 * matches — caller decides what to do (typically: surface the full stdout instead).
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
