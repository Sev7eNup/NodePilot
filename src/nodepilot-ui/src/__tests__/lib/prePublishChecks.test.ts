import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { getPrePublishIssues, getPrePublishLint } from '../../lib/prePublishChecks';
import type { LintResult } from '../../lib/workflowLint';

function activityNode(id: string, activityType: string, extra: Record<string, unknown> = {}): Node {
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { activityType, label: id, ...extra },
  } as unknown as Node;
}

function edge(id: string, source: string, target: string, opts: Partial<{ disabled: boolean }> = {}): Edge {
  return { id, source, target, type: 'labeled', data: { disabled: !!opts.disabled } };
}

const emptyLint: LintResult = { errors: [], warnings: [] };

// The "no trigger" gate moved OUT of the pre-publish checks into the always-on lint
// (`no-trigger` error in workflowLint) — see workflowLint.test.ts. getPrePublishIssues no longer
// emits `no-trigger-prepublish`; it only does the trigger-WITHOUT-outgoing + description hints.

describe('getPrePublishIssues — trigger without outgoing edge', () => {
  it('warns when trigger has no out-edges at all', () => {
    const nodes = [activityNode('t', 'manualTrigger')];
    const issues = getPrePublishIssues(nodes, [], { description: 'x' });
    const dangling = issues.filter((i) => i.code === 'trigger-without-outgoing');
    expect(dangling).toHaveLength(1);
    expect(dangling[0].nodeId).toBe('t');
    expect(dangling[0].severity).toBe('warning');
  });

  it('warns when trigger only has DISABLED out-edges', () => {
    const nodes = [
      activityNode('t', 'manualTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't', 's', { disabled: true })];
    const issues = getPrePublishIssues(nodes, edges, { description: 'x' });
    expect(issues.find((i) => i.code === 'trigger-without-outgoing')).toBeDefined();
  });

  it('does not warn when trigger has at least one active out-edge', () => {
    const nodes = [
      activityNode('t', 'manualTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't', 's')];
    const issues = getPrePublishIssues(nodes, edges, { description: 'x' });
    expect(issues.find((i) => i.code === 'trigger-without-outgoing')).toBeUndefined();
  });

  it('warns per dangling trigger when multiple exist', () => {
    const nodes = [
      activityNode('t1', 'manualTrigger'),
      activityNode('t2', 'scheduleTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't1', 's')];
    const issues = getPrePublishIssues(nodes, edges, { description: 'x' });
    const dangling = issues.filter((i) => i.code === 'trigger-without-outgoing');
    expect(dangling).toHaveLength(1);
    expect(dangling[0].nodeId).toBe('t2');
  });
});

describe('getPrePublishIssues — description hint', () => {
  it('warns when description is empty', () => {
    const nodes = [
      activityNode('t', 'manualTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't', 's')];
    const issues = getPrePublishIssues(nodes, edges, { description: '' });
    const desc = issues.filter((i) => i.code === 'no-description');
    expect(desc).toHaveLength(1);
    expect(desc[0].severity).toBe('warning');
  });

  it('warns when description is only whitespace', () => {
    const nodes = [
      activityNode('t', 'manualTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't', 's')];
    const issues = getPrePublishIssues(nodes, edges, { description: '   \n\t  ' });
    expect(issues.find((i) => i.code === 'no-description')).toBeDefined();
  });

  it('does not warn when description has any non-whitespace content', () => {
    const nodes = [activityNode('t', 'manualTrigger')];
    const edges: Edge[] = [];
    const issues = getPrePublishIssues(nodes, edges, { description: 'Daily report runner' });
    expect(issues.find((i) => i.code === 'no-description')).toBeUndefined();
  });

  it('warns when workflow itself is undefined', () => {
    const issues = getPrePublishIssues([activityNode('t', 'manualTrigger')], [], undefined);
    expect(issues.find((i) => i.code === 'no-description')).toBeDefined();
  });
});

describe('getPrePublishLint', () => {
  it('merges base lint result with pre-publish-only checks', () => {
    const base: LintResult = {
      errors: [{ severity: 'error', code: 'missing-required-config', message: 'broken' }],
      warnings: [{ severity: 'warning', code: 'edge-occluded', message: 'crowded' }],
    };
    const result = getPrePublishLint(
      base,
      [activityNode('s', 'runScript')], // no-trigger is handled by the base lint, not here
      [],
      { description: '' },              // no description → adds a pre-publish warning
    );
    // Base error preserved; the pre-publish checks add no error of their own here.
    expect(result.errors.map((e) => e.code).sort()).toEqual(['missing-required-config']);
    // Original warning preserved, new warning appended
    expect(result.warnings.some((w) => w.code === 'edge-occluded')).toBe(true);
    expect(result.warnings.some((w) => w.code === 'no-description')).toBe(true);
  });

  it('returns base lint unchanged when pre-publish checks are clean', () => {
    const base: LintResult = { errors: [], warnings: [] };
    const nodes = [
      activityNode('t', 'manualTrigger'),
      activityNode('s', 'runScript'),
    ];
    const edges = [edge('e1', 't', 's')];
    const result = getPrePublishLint(base, nodes, edges, { description: 'present' });
    expect(result).toEqual(emptyLint);
  });

  it('preserves severity ordering: errors stay in errors, warnings stay in warnings', () => {
    const result = getPrePublishLint(
      { errors: [], warnings: [] },
      [activityNode('s', 'runScript')], // no trigger
      [],
      { description: '' },
    );
    for (const e of result.errors) expect(e.severity).toBe('error');
    for (const w of result.warnings) expect(w.severity).toBe('warning');
  });
});
