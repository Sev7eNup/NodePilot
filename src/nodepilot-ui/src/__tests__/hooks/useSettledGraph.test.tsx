import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useSettledGraph } from '../../hooks/useSettledGraph';

function node(id: string, x = 0): Node {
  return { id, position: { x, y: 0 }, data: { label: id } };
}

const EDGES: Edge[] = [{ id: 'e1', source: 'a', target: 'b' }];

describe('useSettledGraph', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('starts with the graph it was given', () => {
    const nodes = [node('a')];
    const { result } = renderHook(() => useSettledGraph(nodes, EDGES, 250));
    expect(result.current.nodes).toBe(nodes);
    expect(result.current.edges).toBe(EDGES);
  });

  it('adopts a new graph once the delay elapses', () => {
    const { result, rerender } = renderHook(
      ({ nodes }: { nodes: Node[] }) => useSettledGraph(nodes, EDGES, 250),
      { initialProps: { nodes: [node('a')] } },
    );

    const next = [node('a'), node('b')];
    rerender({ nodes: next });
    expect(result.current.nodes).toHaveLength(1);

    act(() => { vi.advanceTimersByTime(250); });

    expect(result.current.nodes).toBe(next);
  });

  it('settles once after a burst of changes, not once per change', () => {
    // This is the whole point: a drag replaces the node array on every mouse move, and the
    // whole-graph passes hanging off this value must run after the drop, not 60 times a second.
    const { result, rerender } = renderHook(
      ({ nodes }: { nodes: Node[] }) => useSettledGraph(nodes, EDGES, 250),
      { initialProps: { nodes: [node('a', 0)] } },
    );

    for (let x = 1; x <= 20; x++) {
      rerender({ nodes: [node('a', x)] });
      act(() => { vi.advanceTimersByTime(10) });
    }
    // Still the first graph: no pause long enough to settle.
    expect(result.current.nodes[0].position.x).toBe(0);

    act(() => { vi.advanceTimersByTime(250); });

    expect(result.current.nodes[0].position.x).toBe(20);
  });

  it('settles nodes and edges together', () => {
    const { result, rerender } = renderHook(
      ({ nodes, edges }: { nodes: Node[]; edges: Edge[] }) => useSettledGraph(nodes, edges, 250),
      { initialProps: { nodes: [node('a')], edges: [] as Edge[] } },
    );

    const nextNodes = [node('a'), node('b')];
    rerender({ nodes: nextNodes, edges: EDGES });
    act(() => { vi.advanceTimersByTime(250); });

    // A pass that saw edges from one frame and nodes from another would be reading a graph
    // that never existed.
    expect(result.current.nodes).toBe(nextNodes);
    expect(result.current.edges).toBe(EDGES);
  });
});
