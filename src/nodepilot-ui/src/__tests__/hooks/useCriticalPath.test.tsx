import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useCriticalPath, type CriticalPathAnnotation, type CriticalPathStats } from '../../hooks/useCriticalPath';

/**
 * useCriticalPath derives Critical Path Method (CPM) annotations (earliest start, duration,
 * slack, and whether a node sits on the longest chain) from its `nodes`/`edges` args. It is a
 * pure computation: the result is merged into the canvas projection by useDisplayedGraph and is
 * never written into the graph, because a write in controlled mode marks the workflow dirty.
 */

function makeNode(id: string, p95?: number, opts: { disabled?: boolean } = {}): Node {
  const data: Record<string, unknown> = { activityType: 'runScript' };
  if (p95 !== undefined) data.__stats = { p95DurationMs: p95 };
  if (opts.disabled) data.disabled = true;
  return { id, position: { x: 0, y: 0 }, type: 'activity', data };
}

/** The step stats the hook takes as an argument, collected from the fixtures' `__stats`. */
function statsOf(nodes: Node[]): CriticalPathStats {
  return Object.fromEntries(nodes.flatMap((n) => {
    const stats = (n.data as { __stats?: { p95DurationMs: number } }).__stats;
    return stats ? [[n.id, stats]] : [];
  }));
}

function makeEdge(id: string, source: string, target: string, disabled = false): Edge {
  return { id, source, target, ...(disabled ? { data: { disabled: true } } : {}) };
}

type Annotations = ReadonlyMap<string, CriticalPathAnnotation> | null;

function compute(nodes: Node[], edges: Edge[], enabled = true): Annotations {
  return renderHook(() => useCriticalPath(enabled, nodes, edges, statsOf(nodes))).result.current;
}

function annotationOf(annotations: Annotations, id: string): CriticalPathAnnotation | undefined {
  return annotations?.get(id);
}

describe('useCriticalPath', () => {

  it('marks the whole of a linear A→B→C chain critical with accumulating earliestStart', () => {
    const nodes = [makeNode('A', 100), makeNode('B', 200), makeNode('C', 300)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const annotations = compute(nodes, edges);
    const a = annotationOf(annotations, 'A')!;
    const b = annotationOf(annotations, 'B')!;
    const c = annotationOf(annotations, 'C')!;

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

    const annotations = compute(nodes, edges);
    const a = annotationOf(annotations, 'A')!;
    const b = annotationOf(annotations, 'B')!;
    const d = annotationOf(annotations, 'D')!;
    const c = annotationOf(annotations, 'C')!;

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
    // root that still has to be processed and annotated instead of waiting on B's in-degree.
    const nodes = [makeNode('A', 50), makeNode('B', 999, { disabled: true }), makeNode('C', 70)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const annotations = compute(nodes, edges);

    // C reached the queue and was annotated despite its disabled predecessor.
    const c = annotationOf(annotations, 'C');
    expect(c).toBeDefined();
    expect(c!.isCritical).toBe(true);
    expect(c!.earliestStart).toBe(0); // isolated root, no predecessor duration
    expect(c!.duration).toBe(70);

    // A is an isolated terminal node with slack; it finishes before C's longer duration.
    const a = annotationOf(annotations, 'A');
    expect(a).toBeDefined();
    expect(a!.isCritical).toBe(false);
    expect(a!.slack).toBe(20);

    // The disabled node itself is never annotated (excluded from the graph entirely).
    expect(annotationOf(annotations, 'B')).toBeUndefined();
  });

  it('skips a disabled edge so its target is treated as a root (earliestStart 0)', () => {
    // Linear chain A, B, C with the edge from B to C disabled. With that edge skipped, C has no
    // predecessor, so its earliestStart is 0 instead of 300.
    const nodes = [makeNode('A', 100), makeNode('B', 200), makeNode('C', 300)];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C', /* disabled */ true)];

    const annotations = compute(nodes, edges);
    const c = annotationOf(annotations, 'C')!;

    expect(c.earliestStart).toBe(0); // the edge from B to C was not counted
    const b = annotationOf(annotations, 'B')!;
    expect(b.earliestStart).toBe(100); // the edge from A to B still counts
  });

  it('returns null and leaves the nodes untouched when enabled is false', () => {
    const nodes = [makeNode('A', 100), makeNode('B', 200)];
    const edges = [makeEdge('e1', 'A', 'B')];
    const before = nodes.map((n) => n.data);

    expect(compute(nodes, edges, /* enabled */ false)).toBeNull();
    expect(nodes.map((n) => n.data)).toEqual(before);
  });

  it('never writes annotations into the node data it reads', () => {
    const nodes = [makeNode('A', 100), makeNode('B', 200)];
    const edges = [makeEdge('e1', 'A', 'B')];

    compute(nodes, edges);

    for (const n of nodes) expect(n.data).not.toHaveProperty('__criticalPath');
  });

  it('keeps the same result when only node positions change', () => {
    // A drag replaces the node array on every frame. A fresh annotation object per frame would
    // defeat the node components' memo comparators.
    const nodes = [makeNode('A', 100), makeNode('B', 200)];
    const edges = [makeEdge('e1', 'A', 'B')];
    const { result, rerender } = renderHook((p: { nodes: Node[] }) => useCriticalPath(true, p.nodes, edges, statsOf(p.nodes)), {
      initialProps: { nodes },
    });
    const first = result.current;

    rerender({ nodes: nodes.map((n) => ({ ...n, position: { x: n.position.x + 40, y: 10 } })) });

    expect(result.current).toBe(first);
  });

  it('recomputes when a duration changes', () => {
    const nodes = [makeNode('A', 100), makeNode('B', 200)];
    const edges = [makeEdge('e1', 'A', 'B')];
    const { result, rerender } = renderHook((p: { nodes: Node[] }) => useCriticalPath(true, p.nodes, edges, statsOf(p.nodes)), {
      initialProps: { nodes },
    });

    rerender({ nodes: [makeNode('A', 100), makeNode('B', 500)] });

    expect(result.current?.get('B')?.duration).toBe(500);
  });

  it('defaults nodes without __stats to 0ms duration', () => {
    // Without __stats every duration is 0, so every node is critical with zero slack.
    const nodes = [makeNode('A'), makeNode('B'), makeNode('C')];
    const edges = [makeEdge('e1', 'A', 'B'), makeEdge('e2', 'B', 'C')];

    const annotations = compute(nodes, edges);
    for (const id of ['A', 'B', 'C']) {
      const ann = annotationOf(annotations, id)!;
      expect(ann.duration).toBe(0);
      expect(ann.earliestStart).toBe(0);
      expect(ann.isCritical).toBe(true);
      expect(ann.slack).toBe(0);
    }
  });
});
