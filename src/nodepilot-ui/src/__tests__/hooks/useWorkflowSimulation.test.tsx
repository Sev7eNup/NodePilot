import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useWorkflowSimulation } from '../../hooks/useWorkflowSimulation';

function activityNode(id: string, extra: Record<string, unknown> = {}): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { activityType: 'runScript', ...extra } };
}

// Roots are trigger-only, so every runnable preview fixture must start at a trigger node.
function triggerNode(id: string, extra: Record<string, unknown> = {}): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { activityType: 'manualTrigger', ...extra } };
}

function noteNode(id: string): Node {
  return { id, type: 'annotation', position: { x: 0, y: 0 }, data: { activityType: 'note' } };
}

function edge(id: string, source: string, target: string, extra: Record<string, unknown> = {}): Edge {
  return { id, source, target, type: 'labeled', data: { ...extra } };
}

describe('useWorkflowSimulation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('runSimulation_linearGraph_marksAllReachable', () => {
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));

    act(() => result.current.runSimulation());

    expect(result.current.simulation).not.toBeNull();
    expect(Array.from(result.current.simulation!.reachable)).toEqual(expect.arrayContaining(['a', 'b', 'c']));
    expect(result.current.simulation!.skipped.size).toBe(0);
  });

  it('runSimulation_disabledNode_isNeverReachable_andDownstreamSkipped', () => {
    // Node-level disabled flag means the node is never reached, and edges out of it
    // don't contribute. A solo downstream gets marked Skipped.
    const nodes = [
      triggerNode('a'),
      activityNode('b', { disabled: true }),
      activityNode('c'),
    ];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('a')).toBe(true);
    expect(result.current.simulation!.reachable.has('b')).toBe(false);
    // c has no other parent — once b is disabled, c becomes unreachable.
    expect(result.current.simulation!.skipped.has('c')).toBe(true);
  });

  it('runSimulation_disabledEdge_doesNotPropagate', () => {
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c')];
    // Two paths a→b: one direct, one disabled. Direct should still reach b. c disconnected.
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c', { disabled: true })];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('b')).toBe(true);
    expect(result.current.simulation!.skipped.has('c')).toBe(true);
  });

  it('runSimulation_failedConditionEdge_doesNotPropagate', () => {
    // The simulator filters edges whose condition ends in ".failed" — those represent
    // error-handling paths that shouldn't be in the optimistic preview.
    const nodes = [triggerNode('main'), activityNode('handler')];
    const edges = [edge('e', 'main', 'handler', { condition: 'main.failed' })];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('main')).toBe(true);
    // A .failed-only edge into handler must NOT include it in the happy-path simulation.
    expect(result.current.simulation!.skipped.has('handler')).toBe(true);
  });

  it('runSimulation_noteNodes_excludedFromGraph', () => {
    // Note/annotation nodes are layout decoration only — they must not show up in either
    // reachable or skipped sets, otherwise the canvas highlight would visually mark
    // sticky-note shapes as steps.
    const nodes = [activityNode('a'), noteNode('note1')];
    const edges: Edge[] = [];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('note1')).toBe(false);
    expect(result.current.simulation!.skipped.has('note1')).toBe(false);
  });

  it('runSimulation_branchingGraph_orderIsBfsLike', () => {
    // Triangle: a→b, a→c, b→d, c→d. BFS-style traversal visits a first, then b/c at the
    // same depth, then d. Pinning the order shape (not exact b-vs-c) so a refactor that
    // switches to DFS would break the test.
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c'), activityNode('d')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c'), edge('e3', 'b', 'd'), edge('e4', 'c', 'd')];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    const order = result.current.simulation!.order;
    expect(order[0]).toBe('a');
    expect(order[order.length - 1]).toBe('d');
    // b and c both come after a and before d.
    const idxA = order.indexOf('a');
    const idxD = order.indexOf('d');
    const idxB = order.indexOf('b');
    const idxC = order.indexOf('c');
    expect(idxB).toBeGreaterThan(idxA);
    expect(idxC).toBeGreaterThan(idxA);
    expect(idxD).toBeGreaterThan(idxB);
    expect(idxD).toBeGreaterThan(idxC);
  });

  it('runSimulation_emptyGraph_doesNothing', () => {
    const { result } = renderHook(() => useWorkflowSimulation([], []));
    act(() => result.current.runSimulation());
    expect(result.current.simulation).toBeNull();
  });

  it('clearSimulation_resetsResultAndIndex', () => {
    const nodes = [activityNode('a'), activityNode('b')];
    const edges = [edge('e1', 'a', 'b')];
    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));

    act(() => result.current.runSimulation());
    expect(result.current.simulation).not.toBeNull();

    act(() => result.current.clearSimulation());
    expect(result.current.simulation).toBeNull();
    expect(result.current.revealIndex).toBe(0);
  });

  it('revealIndex_advancesOver180msIntervals', () => {
    // The reveal animation steps once per 180ms. Pin the cadence so a refactor that
    // changes the timing surfaces here.
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];
    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));

    act(() => result.current.runSimulation());
    const totalNodes = result.current.simulation!.order.length;

    // Initial: 0. After 180ms: 1. After 360ms: 2. ...
    act(() => { vi.advanceTimersByTime(180); });
    expect(result.current.revealIndex).toBe(1);
    act(() => { vi.advanceTimersByTime(180); });
    expect(result.current.revealIndex).toBe(2);

    // Run to completion — revealIndex stops once it reaches order.length.
    act(() => { vi.advanceTimersByTime(180 * (totalNodes + 5)); });
    expect(result.current.revealIndex).toBe(totalNodes);
  });
});
