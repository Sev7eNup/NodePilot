import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';

/**
 * Harness approach:
 * useCriticalPath computes Critical Path Method (CPM) scheduling annotations — earliest
 * start time, duration, slack, and whether a node sits on the longest chain — from its
 * `nodes`/`edges` args and stamps
 * `__criticalPath` back onto the graph via `useReactFlow().setNodes(updater)`. Rather than
 * spin up a live <ReactFlow> store and read its internal node state (brittle), we mock
 * `useReactFlow` so `setNodes` is a spy that merely CAPTURES the updater the hook pushes.
 * We then replay every captured updater onto the same seeded node array and read the stamped
 * `data.__criticalPath`. This mirrors the codebase precedent in useNodeAnnotations.test.tsx
 * (which reduces captured setNodes updaters) and needs no ReactFlowProvider because the only
 * ReactFlow API the hook touches (useReactFlow) is mocked. Fully deterministic, no timers.
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

    // earliestStart accumulates predecessor durations: 0 → 100 → 300.
    expect(a.earliestStart).toBe(0);
    expect(b.earliestStart).toBe(100);
    expect(c.earliestStart).toBe(300);

    // Durations echo the p95 stats.
    expect(a.duration).toBe(100);
    expect(b.duration).toBe(200);
    expect(c.duration).toBe(300);
  });

  it('puts the longer diamond branch on the critical path and gives the shorter branch slack', () => {
    // A→B→C (long: B=100) and A→D→C (short: D=20).
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

    // Long branch (B) + the shared endpoints are critical.
    expect(a.isCritical).toBe(true);
    expect(b.isCritical).toBe(true);
    expect(c.isCritical).toBe(true);
    expect(b.slack).toBe(0);

    // Short branch (D) is NOT critical and carries positive slack.
    expect(d.isCritical).toBe(false);
    expect(d.slack).toBeGreaterThan(0);
    // Slack = (latestStart 90) − (earliestStart 10) = 80.
    expect(d.slack).toBe(80);
  });

  it('excludes a disabled node from the CPM graph but its successor still reaches the topo queue', () => {
    // Regression guard for the documented in-degree bug: A→B(disabled)→C. B is removed from
    // the graph AND its edges are dropped, so C becomes an isolated root that must still be
    // topologically processed and stamped (rather than being stranded behind B's in-degree).
    const nodes = [makeNode('A', 50), makeNode('B', 999, { disabled: true }), makeNode('C', 70)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const stamped = runAndStamp(nodes, edges);

    // C reached the queue and was stamped despite its disabled predecessor.
    const c = annotationOf(stamped, 'C');
    expect(c).toBeDefined();
    expect(c!.isCritical).toBe(true);
    expect(c!.earliestStart).toBe(0); // isolated root, no predecessor duration
    expect(c!.duration).toBe(70);

    // A is now a terminal isolated node with slack (finishes before C's longer duration).
    const a = annotationOf(stamped, 'A');
    expect(a).toBeDefined();
    expect(a!.isCritical).toBe(false);
    expect(a!.slack).toBe(20);

    // The disabled node itself is never annotated (excluded from the graph entirely).
    expect(annotationOf(stamped, 'B')).toBeUndefined();
  });

  it('skips a disabled edge so its target is treated as a root (earliestStart 0)', () => {
    // A→B→C linear, but the B→C edge is disabled. With the edge skipped, C has no predecessor
    // and its earliestStart is 0 (it would be 300 if the edge were live).
    const nodes = [makeNode('A', 100), makeNode('B', 200), makeNode('C', 300)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C', /* disabled */ true)];

    const stamped = runAndStamp(nodes, edges);
    const c = annotationOf(stamped, 'C')!;

    expect(c.earliestStart).toBe(0); // proves the B→C edge was not counted
    const b = annotationOf(stamped, 'B')!;
    expect(b.earliestStart).toBe(100); // A→B edge is still honored
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
    // No node carries __stats → every duration is 0, every node critical with zero slack.
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
