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
    criticalPath: null,
    annotations: new Map(),
    coverage: null,
    varFlowEdgeIds: new Set<string>(),
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

describe('useDisplayedGraph — critical path and projection-owned keys', () => {
  const annotation = { isCritical: true, slack: 0, earliestStart: 0, duration: 100 };

  function nodeData(nodes: Node[], id: string) {
    return nodes.find((n) => n.id === id)!.data as Record<string, unknown>;
  }

  it('merges the critical-path annotation into the projected node only', () => {
    const criticalPath = new Map([['a', annotation]]);
    const { displayedNodes } = project({ criticalPath });

    expect(nodeData(displayedNodes, 'a').__criticalPath).toBe(annotation);
    expect(nodeData(displayedNodes, 'b')).not.toHaveProperty('__criticalPath');
    // The raw graph stays clean; the overlay lives in the projection alone.
    for (const n of NODES) expect(n.data).not.toHaveProperty('__criticalPath');
  });

  it('drops projection-owned keys that leaked into the raw graph', () => {
    // A leaked value must never be shown as current: with the overlay off and no lint,
    // simulation or heatmap, none of these keys may survive the projection.
    const leaked: Node[] = NODES.map((n) => ({
      ...n,
      data: {
        ...n.data,
        __lintErrors: 3, __lintWarnings: 2, __simulated: 'reachable', __failureTint: 0.5,
        __criticalPath: annotation,
      },
    }));
    const { displayedNodes } = project({ nodes: leaked });

    for (const id of ['a', 'b']) {
      const data = nodeData(displayedNodes, id);
      for (const key of ['__lintErrors', '__lintWarnings', '__simulated', '__failureTint', '__criticalPath']) {
        expect(data, `${id}.${key}`).not.toHaveProperty(key);
      }
      expect(data.label).toBeTruthy(); // authored data untouched
    }
  });

  it('merges the runtime annotations, which feed the failure tint', () => {
    const stats = { totalRuns: 5, failedRuns: 1, failureRate: 0.2, avgDurationMs: 1, p95DurationMs: 1, lastDurationMs: 1 };
    const annotations = new Map([['a', { __stats: stats, __machineColorIdx: 2 }]]);
    const { displayedNodes } = project({ annotations, failureHeatmapEnabled: true });

    expect(nodeData(displayedNodes, 'a').__stats).toBe(stats);
    expect(nodeData(displayedNodes, 'a').__machineColorIdx).toBe(2);
    expect(nodeData(displayedNodes, 'a').__failureTint).toBe(0.2);
    expect(nodeData(displayedNodes, 'b')).not.toHaveProperty('__stats');
    for (const n of NODES) expect(n.data).not.toHaveProperty('__stats');
  });

  it('suppresses lint counts on a node with a live status', () => {
    const issue = (nodeId: string) => ({ severity: 'error' as const, nodeId, message: 'x', code: 'test' });
    const lintResult = { errors: [issue('a'), issue('b')], warnings: [] };
    const annotations = new Map([['a', { __liveStatus: 'Running' }]]);
    const { displayedNodes } = project({ annotations, lintResult });

    expect(nodeData(displayedNodes, 'a').__liveStatus).toBe('Running');
    expect(nodeData(displayedNodes, 'a')).not.toHaveProperty('__lintErrors');
    expect(nodeData(displayedNodes, 'b').__lintErrors).toBe(1);
  });

  it('merges the coverage heatmap', () => {
    const cov = { cls: 'never' as const, executedCount: 0, failedCount: 0, skippedCount: 0, totalExecutions: 4, windowDays: 30, lastExecutedAt: null };
    const { displayedNodes } = project({ coverage: new Map([['a', cov]]) });

    expect(nodeData(displayedNodes, 'a').__coverage).toBe(cov);
    expect(nodeData(displayedNodes, 'b')).not.toHaveProperty('__coverage');
  });

  it('drops runtime annotations that leaked into the raw graph', () => {
    const leaked: Node[] = NODES.map((n) => ({
      ...n,
      data: {
        ...n.data,
        __liveStatus: 'Running', __health: [], __stats: { failureRate: 1, totalRuns: 1 },
        __machineColorIdx: 0, __varFlowRole: 'producer', __workflowEnabled: false, __coverage: { cls: 'never' },
      },
    }));
    const { displayedNodes } = project({ nodes: leaked, failureHeatmapEnabled: true });

    for (const id of ['a', 'b']) {
      const data = nodeData(displayedNodes, id);
      for (const key of ['__liveStatus', '__health', '__stats', '__machineColorIdx', '__varFlowRole',
        '__workflowEnabled', '__coverage', '__failureTint']) {
        expect(data, `${id}.${key}`).not.toHaveProperty(key);
      }
    }
  });

  it('highlights the variable-flow path on the projected edges only', () => {
    const edges: Edge[] = [
      { id: 'e1', source: 'a', target: 'b', data: {} },
      { id: 'e2', source: 'b', target: 'a', data: { __varFlowHighlighted: true } },
    ];
    const { displayedEdges } = project({ edges, varFlowEdgeIds: new Set(['e1']) });

    expect(edgeData(displayedEdges, 'e1').__varFlowHighlighted).toBe(true);
    expect(edgeData(displayedEdges, 'e2')).not.toHaveProperty('__varFlowHighlighted');
    expect(edges[0].data).not.toHaveProperty('__varFlowHighlighted');
  });
});
