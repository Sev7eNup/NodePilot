import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import {
  findReferencedVariables,
  variablesUsedByNode,
  usedVariablesPerNode,
  computeFlowingVariablesPerEdge,
} from '../../lib/variableUsageScan';

function activityNode(id: string, data: Record<string, unknown>): Node {
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { activityType: 'runScript', label: id, ...data },
  } as unknown as Node;
}

describe('findReferencedVariables', () => {
  it('returns empty array for nullish or non-templated input', () => {
    expect(findReferencedVariables(null)).toEqual([]);
    expect(findReferencedVariables(undefined)).toEqual([]);
    expect(findReferencedVariables('plain string, no braces')).toEqual([]);
    expect(findReferencedVariables(42)).toEqual([]);
  });

  it('extracts a single template head from a string', () => {
    expect(findReferencedVariables('Hello {{userStep.output}}')).toEqual(['userStep']);
  });

  it('extracts multiple distinct heads, deduplicated', () => {
    const heads = findReferencedVariables('{{a.output}} and {{b.error}} and {{a.param.x}}');
    expect(heads.sort()).toEqual(['a', 'b']);
  });

  it('strips runtime prefixes (globals, manual)', () => {
    // Only `globals` and `manual` are real engine-injected namespaces (see
    // VariableResolver.BuildStepVariables). `trigger.*` / `webhook.*` look like upstream
    // step references — they used to be filtered by mistake which masked unresolvable
    // templates as "runtime-injected, OK".
    const heads = findReferencedVariables(
      '{{globals.ENV}} {{manual.foo}} {{realStep.output}}',
    );
    expect(heads).toEqual(['realStep']);
  });

  it('does not strip pseudo prefixes (trigger, webhook) — they look like step refs', () => {
    const heads = findReferencedVariables('{{trigger.foo}} {{webhook.body}} {{realStep.output}}').sort();
    expect(heads).toEqual(['realStep', 'trigger', 'webhook']);
  });

  it('walks nested objects and arrays', () => {
    const config = {
      url: 'https://api/{{authStep.output}}',
      headers: { 'X-Trace': '{{traceStep.output}}' },
      body: ['{{listStep.param.first}}', { nested: '{{deepStep.output}}' }],
    };
    const heads = findReferencedVariables(config).sort();
    expect(heads).toEqual(['authStep', 'deepStep', 'listStep', 'traceStep']);
  });

  it('handles dash + underscore in head names', () => {
    expect(findReferencedVariables('{{step-123.output}}')).toEqual(['step-123']);
    expect(findReferencedVariables('{{my_var.output}}')).toEqual(['my_var']);
  });

  it('handles whitespace inside the braces', () => {
    expect(findReferencedVariables('{{ spaced.output }}')).toEqual(['spaced']);
  });

  it('matches head-only template without dotted suffix', () => {
    expect(findReferencedVariables('{{bareHead}}')).toEqual(['bareHead']);
  });
});

describe('variablesUsedByNode', () => {
  it('scans config payload', () => {
    const n = activityNode('s1', { config: { script: 'echo {{upstream.output}}' } });
    expect(variablesUsedByNode(n)).toEqual(['upstream']);
  });

  it('scans targetMachineId + credentialId for templated values', () => {
    const n = activityNode('s1', {
      targetMachineId: '{{hostStep.param.host}}',
      credentialId: '{{credStep.output}}',
      config: {},
    });
    expect(variablesUsedByNode(n).sort()).toEqual(['credStep', 'hostStep']);
  });

  it('returns empty list when no templates anywhere', () => {
    const n = activityNode('s1', { config: { script: 'Get-Date', timeoutSeconds: 60 } });
    expect(variablesUsedByNode(n)).toEqual([]);
  });
});

describe('usedVariablesPerNode', () => {
  it('only adds entries for nodes that reference variables', () => {
    const nodes = [
      activityNode('a', { config: { script: 'Get-Date' } }),                  // no refs
      activityNode('b', { config: { script: '{{a.output}}' } }),               // ref a
      activityNode('c', { config: { script: '{{a.output}} + {{b.output}}' } }), // ref a + b
    ];
    const map = usedVariablesPerNode(nodes);
    expect(map.has('a')).toBe(false);
    expect(map.get('b')).toEqual(['a']);
    expect(map.get('c')!.sort()).toEqual(['a', 'b']);
  });
});

function edge(id: string, source: string, target: string, opts: Partial<{ disabled: boolean }> = {}): Edge {
  return { id, source, target, type: 'labeled', data: { disabled: !!opts.disabled } };
}

describe('computeFlowingVariablesPerEdge', () => {
  it('returns empty map for an empty graph', () => {
    expect(computeFlowingVariablesPerEdge([], [])).toEqual(new Map());
  });

  it('flags an edge as flowing when target consumes the source\'s output', () => {
    const nodes = [
      activityNode('src', { outputVariable: 'srcOut', config: {} }),
      activityNode('tgt', { config: { script: 'echo {{srcOut.output}}' } }),
    ];
    const edges = [edge('e1', 'src', 'tgt')];
    const map = computeFlowingVariablesPerEdge(nodes, edges);
    expect(map.get('e1')).toEqual(['srcOut']);
  });

  it('uses the node id as the head when outputVariable is unset', () => {
    const nodes = [
      activityNode('src', { config: {} }),
      activityNode('tgt', { config: { script: '{{src.output}}' } }),
    ];
    const map = computeFlowingVariablesPerEdge(nodes, [edge('e1', 'src', 'tgt')]);
    expect(map.get('e1')).toEqual(['src']);
  });

  it('does not list an edge when the target ignores the source output', () => {
    const nodes = [
      activityNode('src', { config: {} }),
      activityNode('tgt', { config: { script: 'whoami' } }),
    ];
    const map = computeFlowingVariablesPerEdge(nodes, [edge('e1', 'src', 'tgt')]);
    expect(map.has('e1')).toBe(false);
  });

  it('propagates a head transitively along edge direction', () => {
    // A → B → C, with C consuming {{A.output}}. The A→B edge must light up too,
    // otherwise the visualisation would lie about the data path.
    const nodes = [
      activityNode('a', { config: {} }),
      activityNode('b', { config: { script: 'echo nothing-from-a' } }),
      activityNode('c', { config: { script: '{{a.output}}' } }),
    ];
    const edges = [edge('e_ab', 'a', 'b'), edge('e_bc', 'b', 'c')];
    const map = computeFlowingVariablesPerEdge(nodes, edges);
    expect(map.get('e_ab')).toEqual(['a']);
    expect(map.get('e_bc')).toEqual(['a']);
  });

  it('skips disabled edges', () => {
    const nodes = [
      activityNode('src', { config: {} }),
      activityNode('tgt', { config: { script: '{{src.output}}' } }),
    ];
    const edges = [edge('e1', 'src', 'tgt', { disabled: true })];
    const map = computeFlowingVariablesPerEdge(nodes, edges);
    expect(map.has('e1')).toBe(false);
  });

  it('handles cycles without infinite recursion (both heads flow over both edges)', () => {
    // In a mutual cycle every node is reachable from every other in both directions, so
    // every head exposed in the cycle counts as "flowing" over every edge — accurate to
    // the runtime semantics, where each iteration injects more state into the bus.
    const nodes = [
      activityNode('a', { config: { script: '{{b.output}}' } }),
      activityNode('b', { config: { script: '{{a.output}}' } }),
    ];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'a')];
    const map = computeFlowingVariablesPerEdge(nodes, edges);
    expect(map.get('e1')).toEqual(['a', 'b']);
    expect(map.get('e2')).toEqual(['a', 'b']);
  });

  it('lists multiple flowing heads when both source and an upstream feed downstream usage', () => {
    // A → B → C, where C consumes both {{A.output}} and {{B.output}}.
    const nodes = [
      activityNode('a', { config: {} }),
      activityNode('b', { config: {} }),
      activityNode('c', { config: { script: '{{a.output}} {{b.output}}' } }),
    ];
    const edges = [edge('e_ab', 'a', 'b'), edge('e_bc', 'b', 'c')];
    const map = computeFlowingVariablesPerEdge(nodes, edges);
    // a→b carries A only (B is the source side; nothing has produced B yet from b's POV)
    expect(map.get('e_ab')).toEqual(['a']);
    // b→c carries A (transitive) AND B (direct)
    expect(map.get('e_bc')).toEqual(['a', 'b']);
  });

  it('respects outputVariable rename when computing heads', () => {
    const nodes = [
      activityNode('src-id', { outputVariable: 'renamed', config: {} }),
      activityNode('tgt', { config: { script: '{{renamed.output}}' } }),
    ];
    const map = computeFlowingVariablesPerEdge(nodes, [edge('e1', 'src-id', 'tgt')]);
    expect(map.get('e1')).toEqual(['renamed']);
  });
});
