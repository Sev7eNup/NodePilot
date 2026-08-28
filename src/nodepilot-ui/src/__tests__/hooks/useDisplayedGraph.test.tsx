import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useDisplayedGraph } from '../../hooks/useDisplayedGraph';

/**
 * The projection from `nodes`/`edges` to `displayedNodes`/`displayedEdges` patches purely visual
 * markers into the graph data. Clearing them matters as much as setting them: `useWorkflowHistory`
 * snapshots the projected graph from the React Flow store and writes it back as raw state on undo,
 * so a marker that is only ever set leaks into the raw edges for good (`__detached` makes
 * LabeledEdge apply `pointerEvents: 'none'`). These tests pin both directions for every marker.
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
  /** An edge as it looks after an undo wrote the projected snapshot back into raw state. */
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
