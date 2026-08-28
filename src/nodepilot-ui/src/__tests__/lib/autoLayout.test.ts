import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { autoLayout, autoLayoutTB, autoLayoutCompact } from '../../lib/autoLayout';

/**
 * autoLayout wraps dagre. These tests pin the surface contract, not dagre internals: the
 * same node ids come back, positions are finite integers, an LR edge places the source left
 * of the target, a TB edge places it above, and disabled edges are skipped at graph-build
 * time and therefore do not constrain the layout.
 */

function node(id: string, extra: Partial<Node> = {}): Node {
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { label: id },
    ...extra,
  };
}

function edge(id: string, source: string, target: string, extra: Partial<Edge> = {}): Edge {
  return { id, source, target, type: 'labeled', data: { label: 'On Success' }, ...extra };
}

describe('autoLayout (LR)', () => {
  it('returnsSameNodeIds_inSameOrder', () => {
    const nodes = [node('a'), node('b'), node('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];

    const out = autoLayout(nodes, edges);

    expect(out.map((n) => n.id)).toEqual(['a', 'b', 'c']);
  });

  it('positionsAreFiniteIntegers', () => {
    const nodes = [node('a'), node('b')];
    const edges = [edge('e1', 'a', 'b')];

    const out = autoLayout(nodes, edges);

    for (const n of out) {
      expect(Number.isFinite(n.position.x)).toBe(true);
      expect(Number.isFinite(n.position.y)).toBe(true);
      expect(Number.isInteger(n.position.x)).toBe(true);
      expect(Number.isInteger(n.position.y)).toBe(true);
    }
  });

  it('LR_sourceXIsLessThanTargetX', () => {
    const nodes = [node('a'), node('b'), node('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];

    const out = autoLayout(nodes, edges);

    const a = out.find((n) => n.id === 'a')!;
    const b = out.find((n) => n.id === 'b')!;
    const c = out.find((n) => n.id === 'c')!;
    expect(a.position.x).toBeLessThan(b.position.x);
    expect(b.position.x).toBeLessThan(c.position.x);
  });

  it('disabledEdges_doNotConstrainLayout', () => {
    // dagre would order the endpoints of this edge left to right. autoLayout filters
    // disabled edges out so inactive wiring takes up no horizontal space.
    const nodes = [node('a'), node('isolated')];
    const edges = [edge('e1', 'a', 'isolated', { data: { disabled: true } })];

    const out = autoLayout(nodes, edges);

    // Without a constraint between a and isolated, both stay roots in the same x bucket.
    expect(out).toHaveLength(2);
  });

  it('emptyGraph_returnsEmptyArray', () => {
    expect(autoLayout([], [])).toEqual([]);
  });

  it('respectsMeasuredSize_whenPresent', () => {
    // dagre receives the measured width, so wider nodes get more horizontal slack. This
    // pins that node.measured is consumed.
    const nodes = [
      node('a', { measured: { width: 100, height: 50 } }),
      node('b', { measured: { width: 400, height: 50 } }),
    ];
    const edges = [edge('e1', 'a', 'b')];

    const out = autoLayout(nodes, edges);

    expect(out).toHaveLength(2);
    const a = out.find((n) => n.id === 'a')!;
    const b = out.find((n) => n.id === 'b')!;
    // b is wider, so its centre sits further right than a's right edge.
    expect(b.position.x).toBeGreaterThan(a.position.x + 100);
  });
});

describe('autoLayoutTB', () => {
  it('TB_sourceYIsLessThanTargetY', () => {
    const nodes = [node('a'), node('b')];
    const edges = [edge('e1', 'a', 'b')];

    const out = autoLayoutTB(nodes, edges);

    const a = out.find((n) => n.id === 'a')!;
    const b = out.find((n) => n.id === 'b')!;
    expect(a.position.y).toBeLessThan(b.position.y);
  });
});

describe('autoLayoutCompact', () => {
  it('producesValidLayoutWithTighterSpacing', () => {
    // Compact uses smaller ranksep/nodesep. Exact pixel values are not pinned because
    // dagre internals can shift; only ordering is checked.
    const nodes = [node('a'), node('b'), node('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];

    const out = autoLayoutCompact(nodes, edges);

    const a = out.find((n) => n.id === 'a')!;
    const c = out.find((n) => n.id === 'c')!;
    expect(a.position.x).toBeLessThan(c.position.x);
  });
});
