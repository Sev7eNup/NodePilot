import { describe, it, expect } from 'vitest';
import { resolveVariablePreview, PREVIEW_MAX_CHARS } from '../../lib/variablePreview';
import type { StepExecution } from '../../types/api';

/**
 * resolveVariablePreview is the plumbing behind the hover-preview tooltip. We pin:
 *   - `.output` reads step.output, `.error` / `.errorOutput` reads step.errorOutput
 *   - `.param.X` extracts via $-form or colon-form; falls back to full stdout when neither matches
 *   - bare alias (no suffix) treated as `.output`
 *   - returns null when the channel is empty (no run, or empty stdout/stderr)
 *   - returns null when step is undefined
 *   - long values are truncated to PREVIEW_MAX_CHARS with the truncated flag set
 */

function step(over: Partial<StepExecution> = {}): StepExecution {
  return {
    id: 'sx-1',
    stepId: 'step-42',
    stepName: 'My Step',
    stepType: 'runScript',
    targetMachine: null,
    status: 'Succeeded',
    startedAt: null,
    completedAt: null,
    output: null,
    errorOutput: null,
    traceOutput: null,
    ...over,
  };
}

describe('resolveVariablePreview', () => {
  it('outputSuffix_readsStepOutput', () => {
    const result = resolveVariablePreview(step({ output: 'hello world' }), '{{disk.output}}');
    expect(result?.channel).toBe('stdout');
    expect(result?.value).toBe('hello world');
    expect(result?.truncated).toBe(false);
  });

  it('errorSuffix_readsStepErrorOutput', () => {
    const result = resolveVariablePreview(step({ errorOutput: 'boom' }), '{{disk.error}}');
    expect(result?.channel).toBe('stderr');
    expect(result?.value).toBe('boom');
  });

  it('errorOutputSuffix_alias_alsoReadsErrorOutput', () => {
    const result = resolveVariablePreview(step({ errorOutput: 'boom' }), '{{disk.errorOutput}}');
    expect(result?.channel).toBe('stderr');
    expect(result?.value).toBe('boom');
  });

  it('paramSuffix_dollarFormInOutput_extractsValue', () => {
    // Run-script capture appends "$varName = value" lines. We must isolate the value.
    const stdout = 'some logs\n$hostName = SERVER01\n$other = stuff\n';
    const result = resolveVariablePreview(step({ output: stdout }), '{{host.param.hostName}}');
    expect(result?.channel).toBe('param');
    expect(result?.value).toBe('SERVER01');
    expect(result?.sourceLabel).toBe('param: hostName (stdout-scan)');
  });

  it('paramSuffix_outputParametersJsonTakesPrecedenceOverStdoutScan', () => {
    // Regression: pre-fix the tooltip only scanned stdout for "$x = value" patterns even though
    // the API surfaces structured outputParametersJson. Now the structured snapshot is the
    // primary source — exact values, no heuristic ambiguity. stdout-scan stays as fallback for
    // rows that pre-date the column being populated (or never had params).
    const json = JSON.stringify({ host: 'web01', freeGb: '42' });
    const result = resolveVariablePreview(
      step({ output: '$host = OTHER', outputParametersJson: json }),
      '{{step.param.host}}',
    );
    expect(result?.channel).toBe('param');
    expect(result?.value).toBe('web01'); // not "OTHER" from the stdout-scan
    expect(result?.sourceLabel).toContain('last run');
  });

  it('paramSuffix_malformedOutputParametersJsonFallsBackToStdoutScan', () => {
    // Hover-preview must never throw. A corrupted row should silently degrade to the legacy
    // heuristic, not blank the tooltip.
    const result = resolveVariablePreview(
      step({ output: '$host = SERVER01', outputParametersJson: '{broken json' }),
      '{{step.param.host}}',
    );
    expect(result?.value).toBe('SERVER01');
    expect(result?.sourceLabel).toContain('stdout-scan');
  });

  it('paramSuffix_colonFormInOutput_extractsValue', () => {
    // Some activities emit `name: value` instead of `$name = value`. Pinned because it's the
    // common Get-Service / WMI shape.
    const stdout = 'Status:    Running\nname: BITS\nstartType: Auto';
    const result = resolveVariablePreview(step({ output: stdout }), '{{svc.param.name}}');
    expect(result?.value).toBe('BITS');
  });

  it('paramSuffix_noMatch_fallsBackToFullStdoutWithLabel', () => {
    // If the param can't be isolated, we still surface stdout — empty tooltip would be worse
    // than a noisy one. The sourceLabel must signal the fallback so the user isn't misled.
    const stdout = 'just some output';
    const result = resolveVariablePreview(step({ output: stdout }), '{{svc.param.unknownThing}}');
    expect(result?.value).toBe('just some output');
    expect(result?.sourceLabel).toContain('full stdout');
  });

  it('paramSuffix_emptyStdout_returnsNull', () => {
    const result = resolveVariablePreview(step({ output: null }), '{{svc.param.foo}}');
    expect(result).toBeNull();
  });

  it('bareAlias_treatedAsOutput', () => {
    // `{{step.}}` (no suffix) — degenerate but possible if a user is mid-typing. Defaults
    // to stdout so we still show something.
    const result = resolveVariablePreview(step({ output: 'hi' }), '{{step}}');
    expect(result?.channel).toBe('stdout');
    expect(result?.value).toBe('hi');
  });

  it('emptyOutput_returnsNull', () => {
    const result = resolveVariablePreview(step({ output: '' }), '{{disk.output}}');
    expect(result).toBeNull();
  });

  it('emptyErrorOutput_returnsNull', () => {
    const result = resolveVariablePreview(step({ errorOutput: '' }), '{{disk.error}}');
    expect(result).toBeNull();
  });

  it('undefinedStep_returnsNull', () => {
    // Caller passes undefined when there's no last terminal run for this stepId. Don't crash.
    const result = resolveVariablePreview(undefined, '{{disk.output}}');
    expect(result).toBeNull();
  });

  it('truncatesAtPreviewMaxChars', () => {
    const longOutput = 'a'.repeat(PREVIEW_MAX_CHARS + 50);
    const result = resolveVariablePreview(step({ output: longOutput }), '{{disk.output}}');
    expect(result?.value.length).toBe(PREVIEW_MAX_CHARS);
    expect(result?.truncated).toBe(true);
  });

  it('exactMaxChars_notTruncated', () => {
    const exactOutput = 'a'.repeat(PREVIEW_MAX_CHARS);
    const result = resolveVariablePreview(step({ output: exactOutput }), '{{disk.output}}');
    expect(result?.truncated).toBe(false);
    expect(result?.value.length).toBe(PREVIEW_MAX_CHARS);
  });

  it('paramName_withRegexSpecials_isEscaped', () => {
    // Param names theoretically can contain anything user-defined. Pin that we don't blow
    // up the regex when one shows up.
    const stdout = '$weird.name = found-it';
    const result = resolveVariablePreview(step({ output: stdout }), '{{x.param.weird.name}}');
    // Won't match (the dollar-form regex anchors on `$weird` followed by `=`, but the dot
    // breaks that anchor) — fall back to full stdout. The point is: no thrown exception.
    expect(result).toBeTruthy();
  });

  it('unknownSuffix_fallsBackToOutputWithExplanatoryLabel', () => {
    const result = resolveVariablePreview(step({ output: 'some text' }), '{{x.weird}}');
    expect(result?.channel).toBe('unknown');
    expect(result?.value).toBe('some text');
    expect(result?.sourceLabel).toContain('unrecognized');
  });
});
