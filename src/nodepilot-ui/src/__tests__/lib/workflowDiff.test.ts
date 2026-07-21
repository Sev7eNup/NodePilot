import { describe, it, expect } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import {
  computeDefinitionDiff, diffIsEmpty, hashDefinition,
  isLayoutOnlyNodeChange, buildChangelog, assembleSelectiveDefinition,
} from '../../lib/workflowDiff';

function node(id: string, over: Partial<Node> = {}): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { activityType: 'log', label: id }, ...over } as Node;
}
function edge(id: string, source: string, target: string, over: Partial<Edge> = {}): Edge {
  return { id, source, target, type: 'labeled', data: {}, ...over } as Edge;
}

describe('computeDefinitionDiff', () => {
  it('reports added / removed / unchanged nodes by stable id', () => {
    const base = { nodes: [node('a'), node('b')], edges: [] };
    const current = { nodes: [node('a'), node('c')], edges: [] };
    const diff = computeDefinitionDiff(base, current);

    expect(diff.addedNodes).toEqual(['c']);
    expect(diff.removedNodes).toEqual(['b']);
    expect(diff.changedNodes).toEqual([]);
  });

  it('detects a position-only change (which the old data-only diff missed)', () => {
    const base = { nodes: [node('a', { position: { x: 0, y: 0 } })], edges: [] };
    const current = { nodes: [node('a', { position: { x: 99, y: 0 } })], edges: [] };
    expect(computeDefinitionDiff(base, current).changedNodes).toEqual(['a']);
  });

  it('detects an edge handle change keyed on edge id', () => {
    const base = { nodes: [], edges: [edge('e1', 'a', 'b', { sourceHandle: 'out' })] };
    const current = { nodes: [], edges: [edge('e1', 'a', 'b', { sourceHandle: 'out-2' })] };
    const diff = computeDefinitionDiff(base, current);
    expect(diff.changedEdges).toEqual(['e1']);
    expect(diff.addedEdges).toEqual([]);
  });

  it('reports no differences for identical definitions', () => {
    const def = { nodes: [node('a')], edges: [edge('e1', 'a', 'a')] };
    expect(diffIsEmpty(computeDefinitionDiff(def, structuredClone(def)))).toBe(true);
  });
});

describe('hashDefinition', () => {
  it('is stable for the same definition regardless of node order', () => {
    const a = { nodes: [node('a'), node('b')], edges: [edge('e1', 'a', 'b')] };
    const b = { nodes: [node('b'), node('a')], edges: [edge('e1', 'a', 'b')] };
    expect(hashDefinition(a)).toBe(hashDefinition(b));
  });

  it('changes when a node config changes', () => {
    const a = { nodes: [node('a', { data: { activityType: 'log', config: { message: 'x' } } })], edges: [] };
    const b = { nodes: [node('a', { data: { activityType: 'log', config: { message: 'y' } } })], edges: [] };
    expect(hashDefinition(a)).not.toBe(hashDefinition(b));
  });

  it('changes when a node moves (stale-proposal guard)', () => {
    const a = { nodes: [node('a', { position: { x: 0, y: 0 } })], edges: [] };
    const b = { nodes: [node('a', { position: { x: 10, y: 0 } })], edges: [] };
    expect(hashDefinition(a)).not.toBe(hashDefinition(b));
  });
});

describe('isLayoutOnlyNodeChange', () => {
  it('is true when only the position differs', () => {
    expect(isLayoutOnlyNodeChange(node('a', { position: { x: 0, y: 0 } }), node('a', { position: { x: 50, y: 9 } }))).toBe(true);
  });
  it('is false when data changes too', () => {
    const prev = node('a', { position: { x: 0, y: 0 }, data: { activityType: 'log', label: 'a' } });
    const next = node('a', { position: { x: 50, y: 0 }, data: { activityType: 'log', label: 'renamed' } });
    expect(isLayoutOnlyNodeChange(prev, next)).toBe(false);
  });
});

describe('buildChangelog', () => {
  it('labels adds/removes/changes and flags layout-only changes', () => {
    const base = { nodes: [node('a', { data: { activityType: 'log', label: 'Keep' } }), node('b', { data: { activityType: 'log', label: 'Gone' } })], edges: [edge('e1', 'a', 'b')] };
    const current = {
      nodes: [
        node('a', { position: { x: 200, y: 0 }, data: { activityType: 'log', label: 'Keep' } }), // moved → layout-only
        node('c', { data: { activityType: 'restApi', label: 'New' } }),                          // added
      ],
      edges: [edge('e2', 'a', 'c')], // e1 removed, e2 added
    };
    const log = buildChangelog(base, current);
    const byKind = (k: string) => log.filter((e) => e.kind === k).map((e) => e.id);
    expect(byKind('add').sort()).toEqual(['c', 'e2']);
    expect(byKind('remove').sort()).toEqual(['b', 'e1']);
    const changedA = log.find((e) => e.id === 'a');
    expect(changedA?.kind).toBe('change');
    expect(changedA?.layoutOnly).toBe(true);
    const addedC = log.find((e) => e.id === 'c');
    expect(addedC?.activityType).toBe('restApi');
    expect(addedC?.label).toBe('New');
  });
});

describe('assembleSelectiveDefinition', () => {
  const current = { nodes: [node('t1'), node('keep')], edges: [edge('e0', 't1', 'keep')] };
  const proposed = {
    nodes: [node('t1'), node('keep'), node('new', { data: { activityType: 'log', label: 'New' } })],
    edges: [edge('e0', 't1', 'keep'), edge('e1', 'keep', 'new')],
  };

  it('applies only the selected adds (and keeps the rest of the canvas)', () => {
    const res = assembleSelectiveDefinition(current, proposed, new Set(['new', 'e1']));
    expect(res.nodes.map((n) => n.id).sort()).toEqual(['keep', 'new', 't1']);
    expect(res.edges.map((e) => e.id).sort()).toEqual(['e0', 'e1']);
    expect(res.droppedEdges).toBe(0);
  });

  it('drops an added edge whose node was not selected (dangling)', () => {
    const res = assembleSelectiveDefinition(current, proposed, new Set(['e1'])); // edge but NOT its 'new' node
    expect(res.nodes.map((n) => n.id).sort()).toEqual(['keep', 't1']);
    expect(res.edges.map((e) => e.id).sort()).toEqual(['e0']); // e1 dropped
    expect(res.droppedEdges).toBe(1);
  });

  it('removing a node also drops its incident edges', () => {
    const cur = { nodes: [node('a'), node('b')], edges: [edge('e', 'a', 'b')] };
    const prop = { nodes: [node('a')], edges: [] }; // b removed
    const res = assembleSelectiveDefinition(cur, prop, new Set(['b'])); // select the node removal only
    expect(res.nodes.map((n) => n.id)).toEqual(['a']);
    expect(res.edges).toEqual([]); // incident edge e gone
    expect(res.droppedEdges).toBe(1);
  });
});
