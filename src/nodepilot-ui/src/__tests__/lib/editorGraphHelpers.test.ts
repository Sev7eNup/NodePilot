import { describe, it, expect } from 'vitest';
import type { Edge } from '@xyflow/react';
import {
  getDownstreamNodeIds,
  renameVariableInNodeData,
  renameVariableInEdge,
} from '../../lib/editorGraphHelpers';

const mkEdge = (id: string, source: string, target: string): Edge =>
  ({ id, source, target }) as Edge;

describe('getDownstreamNodeIds', () => {
  it('walks a linear chain and excludes the start node', () => {
    const edges = [mkEdge('e1', 'A', 'B'), mkEdge('e2', 'B', 'C'), mkEdge('e3', 'C', 'D')];
    const result = getDownstreamNodeIds('A', edges);
    expect([...result].sort()).toEqual(['B', 'C', 'D']);
    expect(result.has('A')).toBe(false);
  });

  it('visits a diamond join node exactly once', () => {
    // A -> B, A -> C, B -> D, C -> D  (D reachable via two paths)
    const edges = [
      mkEdge('e1', 'A', 'B'),
      mkEdge('e2', 'A', 'C'),
      mkEdge('e3', 'B', 'D'),
      mkEdge('e4', 'C', 'D'),
    ];
    const result = getDownstreamNodeIds('A', edges);
    expect([...result].sort()).toEqual(['B', 'C', 'D']);
    // Set semantics + the !has guard mean D is present a single time.
    expect(result.size).toBe(3);
  });

  it('terminates on a cycle and includes the start id once it is revisited', () => {
    const edges = [mkEdge('e1', 'A', 'B'), mkEdge('e2', 'B', 'C'), mkEdge('e3', 'C', 'A')];
    const result = getDownstreamNodeIds('A', edges);
    // A is reachable again through the cycle, so it appears in the downstream set.
    expect([...result].sort()).toEqual(['A', 'B', 'C']);
  });

  it('returns an empty set when the start node has no outgoing edges', () => {
    const edges = [mkEdge('e1', 'X', 'Y')];
    const result = getDownstreamNodeIds('Z', edges);
    expect(result.size).toBe(0);
  });
});

describe('renameVariableInNodeData', () => {
  it('rewrites {{old.…}} tokens inside config for a simple rename', () => {
    const data = { label: 'Step', config: { script: 'echo {{old.output}} {{old.error}}' } };
    const result = renameVariableInNodeData(data, 'old', 'fresh');
    expect((result.config as { script: string }).script).toBe('echo {{fresh.output}} {{fresh.error}}');
    // Non-config fields are carried through untouched.
    expect(result.label).toBe('Step');
  });

  it('escapes regex-special characters in the alias (dotted alias)', () => {
    const data = { config: { a: '{{a.b.output}}', b: '{{a.bX.output}}' } };
    const result = renameVariableInNodeData(data, 'a.b', 'c');
    const cfg = result.config as Record<string, string>;
    expect(cfg.a).toBe('{{c.output}}');
    // "a.bX" is a different prefix — the escaped dot prevents a spurious match.
    expect(cfg.b).toBe('{{a.bX.output}}');
  });

  it('escapes regex-special characters in the alias (plus sign)', () => {
    const data = { config: { hit: '{{x+y.output}}', miss: '{{xy.output}}' } };
    const result = renameVariableInNodeData(data, 'x+y', 'z');
    const cfg = result.config as Record<string, string>;
    expect(cfg.hit).toBe('{{z.output}}');
    // Without escaping, "x+y" would match "xy" as a regex — escaping keeps it literal.
    expect(cfg.miss).toBe('{{xy.output}}');
  });

  it('does not rename a longer-prefix alias (oldAliasX stays)', () => {
    const data = { config: { keep: '{{oldX.output}}', change: '{{old.output}}' } };
    const result = renameVariableInNodeData(data, 'old', 'new');
    const cfg = result.config as Record<string, string>;
    expect(cfg.keep).toBe('{{oldX.output}}');
    expect(cfg.change).toBe('{{new.output}}');
  });

  it('leaves config value-equal when the token is absent', () => {
    const data = { config: { script: 'echo hello' } };
    const result = renameVariableInNodeData(data, 'old', 'new');
    expect(result.config).toEqual({ script: 'echo hello' });
  });

  it('only rewrites the config subtree, not sibling data fields', () => {
    const data = { outputVariable: '{{old.output}}', config: { script: '{{old.output}}' } };
    const result = renameVariableInNodeData(data, 'old', 'new');
    // Sibling field is outside config → untouched.
    expect(result.outputVariable).toBe('{{old.output}}');
    expect((result.config as { script: string }).script).toBe('{{new.output}}');
  });

  it('returns the original data object when the rewrite breaks JSON round-trip', () => {
    // A newAlias containing a double-quote produces invalid JSON after the raw string
    // replace → JSON.parse throws → the catch returns the untouched original.
    const data = { config: { script: '{{old.output}}' } };
    const result = renameVariableInNodeData(data, 'old', 'x"y');
    expect(result).toBe(data);
  });
});

describe('renameVariableInEdge', () => {
  it('rewrites the legacy condition shortcut ".success"', () => {
    const edge = { id: 'e1', source: 'a', target: 'b', data: { condition: 'old.success' } } as unknown as Edge;
    const result = renameVariableInEdge(edge, 'old', 'new');
    expect((result.data as { condition: string }).condition).toBe('new.success');
    expect(result).not.toBe(edge); // changed → new object
  });

  it('rewrites the legacy condition shortcut ".failed"', () => {
    const edge = { id: 'e1', source: 'a', target: 'b', data: { condition: 'old.failed' } } as unknown as Edge;
    const result = renameVariableInEdge(edge, 'old', 'new');
    expect((result.data as { condition: string }).condition).toBe('new.failed');
  });

  it('recursively replaces matching {variable} operands in conditionExpression', () => {
    const edge = {
      id: 'e1', source: 'a', target: 'b',
      data: {
        conditionExpression: {
          type: 'group',
          operator: 'AND',
          conditions: [
            { type: 'comparison', left: { variable: 'old' }, right: { literal: '1' } },
            { type: 'comparison', left: { variable: 'other' }, right: { literal: '2' } },
          ],
        },
      },
    } as unknown as Edge;
    const result = renameVariableInEdge(edge, 'old', 'new');
    const expr = (result.data as Record<string, unknown>).conditionExpression as {
      conditions: Array<{ left: { variable: string } }>;
    };
    expect(expr.conditions[0].left.variable).toBe('new');
    expect(expr.conditions[1].left.variable).toBe('other'); // untouched
    expect(result).not.toBe(edge);
  });

  it('returns the SAME edge object when nothing matched', () => {
    const edge = {
      id: 'e1', source: 'a', target: 'b',
      data: { condition: 'someoneelse.success', conditionExpression: { left: { variable: 'other' } } },
    } as unknown as Edge;
    const result = renameVariableInEdge(edge, 'old', 'new');
    expect(result).toBe(edge); // referential no-op via the `changed` flag
  });

  it('returns the same edge when it carries no data', () => {
    const edge = { id: 'e1', source: 'a', target: 'b' } as Edge;
    const result = renameVariableInEdge(edge, 'old', 'new');
    expect(result).toBe(edge);
  });
});
