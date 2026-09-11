import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useDisplayedGraph } from '../../hooks/useDisplayedGraph';

/**
 * The projection from `nodes`/`edges` to `displayedNodes`/`displayedEdges` patches purely visual
 * markers into the graph data. Clearing them matters as much as setting them: a marker that is
 * only ever set survives in any graph that once carried it (`__detached` makes LabeledEdge apply
 * `pointerEvents: 'none'`). These tests pin both directions for every marker.
 */

const NODES: Node[] = [
  { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A', activityType: 'runScript' } },
  { id: 'b', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'B', activityType: 'delay' } },
];

function baseArgs(over: Partial<Parameters<typeof useDisplayedGraph>[0]> = {}) {
  return {
    nodes: NODES,
    edges: [] as Edge[],
    edgesAnimated: false,
    hiddenActivityTypes: new Set<string>(),
    dataFlowOverlayEnabled: false,
    simulation: null,
    revealIndex: 0,
    lintResult: { errors: [], warnings: [] },
    failureHeatmapEnabled: false,
    ...over,
  };
}

function project(over: Partial<Parameters<typeof useDisplayedGraph>[0]> = {}) {
  return renderHook(() => useDisplayedGraph(baseArgs(over))).result.current;
}

function edgeData(edges: Edge[], id: string) {
  return (edges.find((e) => e.id === id)!.data ?? {}) as Record<string, unknown>;
}

describe('useDisplayedGraph — edge detach marker', () => {
  const cleanEdge: Edge = { id: 'e1', source: 'a', target: 'b', type: 'labeled', data: { label: 'On Success' } };
  /** An edge carrying a marker from an earlier session, e.g. a definition saved before the
   *  projection stopped leaking into persisted state. */
  const staleEdge: Edge = { ...cleanEdge, data: { label: 'On Success', __detached: true } };

  it('detachedEdgeId_marksThatEdge', () => {
    const { displayedEdges } = project({ edges: [cleanEdge], detachedEdgeId: 'e1' });
    expect(edgeData(displayedEdges, 'e1').__detached).toBe(true);
  });

  it('noDetachInProgress_stripsAStaleMarkerFromRawEdges', () => {
    // Without this cleanup branch the edge stays dimmed after an undo and keeps
    // `pointerEvents: 'none'`, so it cannot be clicked until the page reloads.
    const { displayedEdges } = project({ edges: [staleEdge], detachedEdgeId: null });
    expect(edgeData(displayedEdges, 'e1')).not.toHaveProperty('__detached');
    expect(edgeData(displayedEdges, 'e1').label).toBe('On Success'); // rest of the data untouched
  });

  it('anotherEdgeDetached_stillStripsTheStaleMarker', () => {
    const other: Edge = { id: 'e2', source: 'b', target: 'a', type: 'labeled', data: {} };
    const { displayedEdges } = project({ edges: [staleEdge, other], detachedEdgeId: 'e2' });

    expect(edgeData(displayedEdges, 'e1')).not.toHaveProperty('__detached');
    expect(edgeData(displayedEdges, 'e2').__detached).toBe(true);
  });

  it('cleanEdgeWithoutDetach_isPassedThroughUntouched', () => {
    // The projection must return the same object when nothing changes, otherwise every edge
    // re-renders on every pass.
    const withPorts: Edge = { ...cleanEdge, sourceHandle: 'right', targetHandle: 'left', animated: false, hidden: false };
    const { displayedEdges } = project({ edges: [withPorts], detachedEdgeId: null });
    expect(displayedEdges[0]).toBe(withPorts);
  });
});

describe('useDisplayedGraph — dock target marker', () => {
  it('dockTargetNodeId_setsTheClassOnThatNodeOnly', () => {
    const { displayedNodes } = project({ dockTargetNodeId: 'b' });
    expect(displayedNodes.find((n) => n.id === 'b')!.className).toBe('np-dock-target');
    expect(displayedNodes.find((n) => n.id === 'a')!.className).toBeUndefined();
  });

  it('noDockTarget_clearsTheClassEverywhere', () => {
    // Same cleanup direction as above: the ring must not stay on a node after an undo.
    const stale: Node[] = NODES.map((n) => ({ ...n, className: 'np-dock-target' }));
    const { displayedNodes } = project({ nodes: stale, dockTargetNodeId: null });
    for (const n of displayedNodes) expect(n.className).toBeUndefined();
  });
});

describe('useDisplayedGraph — identity stability', () => {
  const EDGES: Edge[] = [
    { id: 'e1', source: 'a', target: 'b', type: 'labeled', data: { label: 'On Success' } },
  ];

  it('keeps every edge object when only the node array changes', () => {
    // A drag replaces the node array on every frame. Rebuilding the edges there would churn
    // React Flow's edge lookup and re-render every EdgeWrapper for a move that touched none
    // of them.
    const args = baseArgs({ edges: EDGES, edgesAnimated: false });
    const { result, rerender } = renderHook((p: Parameters<typeof useDisplayedGraph>[0]) => useDisplayedGraph(p), {
      initialProps: args,
    });
    const before = result.current.displayedEdges;

    const moved = NODES.map((n) => (n.id === 'a' ? { ...n, position: { x: 40, y: 0 } } : n));
    rerender({ ...args, nodes: moved });

    expect(result.current.displayedEdges[0]).toBe(before[0]);
  });

  it('still hides edges that touch a filtered-out node', () => {
    const { displayedEdges } = project({ edges: EDGES, hiddenActivityTypes: new Set(['delay']) });
    expect(displayedEdges[0].hidden).toBe(true);
  });

  it('re-projects edges when the activity-type filter changes', () => {
    const args = baseArgs({ edges: EDGES });
    const { result, rerender } = renderHook((p: Parameters<typeof useDisplayedGraph>[0]) => useDisplayedGraph(p), {
      initialProps: args,
    });
    expect(result.current.displayedEdges[0].hidden).toBeFalsy();

    rerender({ ...args, hiddenActivityTypes: new Set(['delay']) });

    expect(result.current.displayedEdges[0].hidden).toBe(true);
  });

  it('counts lint issues per node without scanning the issue lists per node', () => {
    const lintResult = {
      errors: [{ severity: 'error' as const, nodeId: 'a', code: 'x', message: 'boom' }],
      warnings: [
        { severity: 'warning' as const, nodeId: 'a', code: 'y', message: 'meh' },
        { severity: 'warning' as const, nodeId: 'b', code: 'y', message: 'meh' },
      ],
    };
    const { displayedNodes } = project({ lintResult });

    const a = displayedNodes.find((n) => n.id === 'a')!.data as Record<string, unknown>;
    const b = displayedNodes.find((n) => n.id === 'b')!.data as Record<string, unknown>;
    expect(a.__lintErrors).toBe(1);
    expect(a.__lintWarnings).toBe(1);
    expect(b.__lintErrors).toBeUndefined();
    expect(b.__lintWarnings).toBe(1);
  });
});
