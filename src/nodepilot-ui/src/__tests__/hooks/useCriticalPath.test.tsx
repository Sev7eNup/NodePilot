import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';

/**
 * Harness approach:
 * useCriticalPath derives Critical Path Method (CPM) annotations (earliest start, duration,
 * slack, and whether a node sits on the longest chain) from its `nodes`/`edges` args and stamps
 * `__criticalPath` back onto the graph via `useReactFlow().setNodes(updater)`. These tests mock
 * `useReactFlow` so `setNodes` only captures the updaters, then replay them onto the seeded node
 * array to read the stamped `data.__criticalPath`. No ReactFlowProvider and no timers needed.
 */

const setNodes = vi.fn();
vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  return { ...actual, useReactFlow: () => ({ setNodes }) };
});

import { useCriticalPath } from '../../hooks/useCriticalPath';

interface CriticalPathAnnotation {
  isCritical: boolean;
  slack: number;
  earliestStart: number;
  duration: number;
}

function makeNode(id: string, p95?: number, opts: { disabled?: boolean } = {}): Node {
  const data: Record<string, unknown> = { activityType: 'runScript' };
  if (p95 !== undefined) data.__stats = { p95DurationMs: p95 };
  if (opts.disabled) data.disabled = true;
  return { id, position: { x: 0, y: 0 }, type: 'activity', data };
}

function makeEdge(id: string, source: string, target: string, disabled = false): Edge {
  return { id, source, target, ...(disabled ? { data: { disabled: true } } : {}) };
}

/**
 * Render the hook, then replay all captured setNodes updaters onto the seeded nodes so the
 * `__criticalPath` stamps become observable. Returns the stamped node array.
 */
function runAndStamp(nodes: Node[], edges: Edge[], enabled = true): Node[] {
  setNodes.mockReset();
  renderHook(() => useCriticalPath('wf-1', enabled, nodes, edges));
  const updaters = setNodes.mock.calls.map((c) => c[0]);
  return updaters.reduce<Node[]>(
    (nds, u) => (typeof u === 'function' ? u(nds) : u ?? nds),
    nodes,
  );
}

function annotationOf(nodes: Node[], id: string): CriticalPathAnnotation | undefined {
  const n = nodes.find((x) => x.id === id);
  return (n?.data as Record<string, unknown> | undefined)?.__criticalPath as
    | CriticalPathAnnotation
    | undefined;
}

describe('useCriticalPath', () => {
  beforeEach(() => {
    setNodes.mockReset();
  });

  it('marks the whole of a linear A→B→C chain critical with accumulating earliestStart', () => {
    const nodes = [makeNode('A', 100), makeNode('B', 200), makeNode('C', 300)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const stamped = runAndStamp(nodes, edges);
    const a = annotationOf(stamped, 'A')!;
    const b = annotationOf(stamped, 'B')!;
    const c = annotationOf(stamped, 'C')!;

    // Every node on a single chain is on the critical path (zero slack).
    expect(a.isCritical).toBe(true);
    expect(b.isCritical).toBe(true);
    expect(c.isCritical).toBe(true);
    expect(a.slack).toBe(0);
    expect(b.slack).toBe(0);
    expect(c.slack).toBe(0);

    // earliestStart accumulates predecessor durations: 0, then 100, then 300.
    expect(a.earliestStart).toBe(0);
    expect(b.earliestStart).toBe(100);
    expect(c.earliestStart).toBe(300);

    // Durations come straight from the p95 stats.
    expect(a.duration).toBe(100);
    expect(b.duration).toBe(200);
    expect(c.duration).toBe(300);
  });

  it('puts the longer diamond branch on the critical path and gives the shorter branch slack', () => {
    // Long branch A, B, C with B at 100; short branch A, D, C with D at 20.
    const nodes = [makeNode('A', 10), makeNode('B', 100), makeNode('D', 20), makeNode('C', 10)];
    const edges = [
      makeEdge('e1', 'A', 'B'),
      makeEdge('e2', 'B', 'C'),
      makeEdge('e3', 'A', 'D'),
      makeEdge('e4', 'D', 'C'),
    ];

    const stamped = runAndStamp(nodes, edges);
    const a = annotationOf(stamped, 'A')!;
    const b = annotationOf(stamped, 'B')!;
    const d = annotationOf(stamped, 'D')!;
    const c = annotationOf(stamped, 'C')!;

    // The long branch (B) and the shared endpoints are critical.
    expect(a.isCritical).toBe(true);
    expect(b.isCritical).toBe(true);
    expect(c.isCritical).toBe(true);
    expect(b.slack).toBe(0);

    // The short branch (D) is not critical and carries positive slack.
    expect(d.isCritical).toBe(false);
    expect(d.slack).toBeGreaterThan(0);
    // Slack is latestStart 90 minus earliestStart 10.
    expect(d.slack).toBe(80);
  });

  it('excludes a disabled node from the CPM graph but its successor still reaches the topo queue', () => {
    // Chain A, B, C with B disabled. B and its edges leave the graph, so C becomes an isolated
    // root that still has to be processed and stamped instead of waiting on B's in-degree.
    const nodes = [makeNode('A', 50), makeNode('B', 999, { disabled: true }), makeNode('C', 70)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const stamped = runAndStamp(nodes, edges);

    // C reached the queue and was stamped despite its disabled predecessor.
    const c = annotationOf(stamped, 'C');
    expect(c).toBeDefined();
    expect(c!.isCritical).toBe(true);
    expect(c!.earliestStart).toBe(0); // isolated root, no predecessor duration
    expect(c!.duration).toBe(70);

    // A is an isolated terminal node with slack; it finishes before C's longer duration.
    const a = annotationOf(stamped, 'A');
    expect(a).toBeDefined();
    expect(a!.isCritical).toBe(false);
    expect(a!.slack).toBe(20);

    // The disabled node itself is never annotated (excluded from the graph entirely).
    expect(annotationOf(stamped, 'B')).toBeUndefined();
  });

  it('skips a disabled edge so its target is treated as a root (earliestStart 0)', () => {
    // Linear chain A, B, C with the edge from B to C disabled. With that edge skipped, C has no
    // predecessor, so its earliestStart is 0 instead of 300.
    const nodes = [makeNode('A', 100), makeNode('B', 200), makeNode('C', 300)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C', /* disabled */ true)];

    const stamped = runAndStamp(nodes, edges);
    const c = annotationOf(stamped, 'C')!;

    expect(c.earliestStart).toBe(0); // the edge from B to C was not counted
    const b = annotationOf(stamped, 'B')!;
    expect(b.earliestStart).toBe(100); // the edge from A to B still counts
  });

  it('stamps nothing and clears prior annotations when enabled is false', () => {
    const a = makeNode('A', 100);
    // Seed a stale annotation that the disabled pass must strip.
    (a.data as Record<string, unknown>).__criticalPath = {
      isCritical: true,
      slack: 0,
      earliestStart: 0,
      duration: 100,
    };
    const nodes = [a];
    const edges: Edge[] = [];

    const stamped = runAndStamp(nodes, edges, /* enabled */ false);

    // The clearing pass removes the existing __criticalPath key.
    expect(annotationOf(stamped, 'A')).toBeUndefined();
    expect('__criticalPath' in (stamped[0].data as Record<string, unknown>)).toBe(false);
  });

  it('defaults nodes without __stats to 0ms duration', () => {
    // Without __stats every duration is 0, so every node is critical with zero slack.
    const nodes = [makeNode('A'), makeNode('B'), makeNode('C')];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const stamped = runAndStamp(nodes, edges);
    for (const id of ['A', 'B', 'C']) {
      const ann = annotationOf(stamped, id)!;
      expect(ann.duration).toBe(0);
      expect(ann.earliestStart).toBe(0);
      expect(ann.isCritical).toBe(true);
      expect(ann.slack).toBe(0);
    }
  });
});
