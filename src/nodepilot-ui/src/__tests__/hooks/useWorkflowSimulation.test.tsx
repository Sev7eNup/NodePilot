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
    // A disabled node is never reached and its outgoing edges do not propagate, so a
    // downstream node with no other parent is marked skipped.
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
    // c has no other parent, so it becomes unreachable once b is disabled.
    expect(result.current.simulation!.skipped.has('c')).toBe(true);
  });

  it('runSimulation_disabledEdge_doesNotPropagate', () => {
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c')];
    // Two edges leave a: a live one to b and a disabled one to c, so only b is reached.
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c', { disabled: true })];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('b')).toBe(true);
    expect(result.current.simulation!.skipped.has('c')).toBe(true);
  });

  it('runSimulation_failedConditionEdge_doesNotPropagate', () => {
    // The simulator drops edges whose condition ends in ".failed": they are error-handling
    // paths and stay out of the optimistic preview.
    const nodes = [triggerNode('main'), activityNode('handler')];
    const edges = [edge('e', 'main', 'handler', { condition: 'main.failed' })];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('main')).toBe(true);
    // An edge into handler carrying only a .failed condition keeps it out of the preview.
    expect(result.current.simulation!.skipped.has('handler')).toBe(true);
  });

  it('runSimulation_noteNodes_excludedFromGraph', () => {
    // Annotation nodes are layout decoration and belong in neither the reachable nor the
    // skipped set, otherwise the canvas highlight would mark sticky notes as steps.
    const nodes = [activityNode('a'), noteNode('note1')];
    const edges: Edge[] = [];

    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));
    act(() => result.current.runSimulation());

    expect(result.current.simulation!.reachable.has('note1')).toBe(false);
    expect(result.current.simulation!.skipped.has('note1')).toBe(false);
  });

  it('runSimulation_branchingGraph_orderIsBfsLike', () => {
    // Diamond graph: a to b, a to c, b to d, c to d. Traversal is breadth-first, so a comes
    // first, b and c follow at the same depth, then d. The assertions pin that shape rather
    // than the exact position of b against c.
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
    // The reveal animation advances one node per 180ms.
    const nodes = [triggerNode('a'), activityNode('b'), activityNode('c')];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c')];
    const { result } = renderHook(() => useWorkflowSimulation(nodes, edges));

    act(() => result.current.runSimulation());
    const totalNodes = result.current.simulation!.order.length;

    // The index starts at 0, reaches 1 after 180ms and 2 after 360ms.
    act(() => { vi.advanceTimersByTime(180); });
    expect(result.current.revealIndex).toBe(1);
    act(() => { vi.advanceTimersByTime(180); });
    expect(result.current.revealIndex).toBe(2);

    // Run to completion: revealIndex stops once it reaches order.length.
    act(() => { vi.advanceTimersByTime(180 * (totalNodes + 5)); });
    expect(result.current.revealIndex).toBe(totalNodes);
  });
});
