import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { buildCollapsedGraphView } from '../../lib/collapsedGraphView';

function group(id: string, collapsed: boolean): Node {
  return {
    id,
    type: 'group',
    position: { x: 0, y: 0 },
    style: { width: 400, height: 240 },
    data: { label: id, collapsed },
  };
}

function node(id: string, parentId?: string): Node {
  return { id, type: 'activity', position: { x: 20, y: 40 }, parentId, data: { label: id } };
}

function edge(id: string, source: string, target: string): Edge {
  return { id, source, target, type: 'labeled', data: {} };
}

describe('buildCollapsedGraphView', () => {
  it('hides child nodes and rewires external edges to the collapsed group', () => {
    const view = buildCollapsedGraphView(
      [group('g', true), node('a', 'g'), node('b', 'g'), node('outside')],
      [edge('internal', 'a', 'b'), edge('in', 'outside', 'a'), edge('out', 'b', 'outside')],
    );

    expect(view.nodes.map((n) => n.id)).toEqual(['g', 'outside']);
    expect(view.edges.map((e) => [e.source, e.target])).toEqual([
      ['outside', 'g'],
      ['g', 'outside'],
    ]);
    expect(view.edges.some((e) => e.id === 'internal')).toBe(false);
  });

  it('returns original arrays when no group is collapsed', () => {
    const nodes = [group('g', false), node('a', 'g')];
    const edges = [edge('e', 'g', 'a')];
    expect(buildCollapsedGraphView(nodes, edges)).toEqual({ nodes, edges });
  });
});
