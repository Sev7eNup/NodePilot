import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useDisplayedGraph } from '../../hooks/useDisplayedGraph';

/**
 * Die Projektion `nodes`/`edges` → `displayedNodes`/`displayedEdges` patcht rein visuelle
 * Marker in die Graph-Daten. Kritisch daran ist die **Aufräum**-Richtung, nicht das Setzen:
 *
 * `useWorkflowHistory` snapshottet über React Flows Store, also über den PROJIZIERTEN
 * Graphen, und schreibt ihn beim Undo in den Rohzustand zurück. Ein Marker, den die
 * Projektion nur setzt und nie entfernt, wandert damit dauerhaft in die rohen Edges — beim
 * Edge-Detach hatte genau das eine Edge nach „Umhängen + Undo" bis zum Reload unklickbar
 * gemacht (`__detached` ⇒ `pointerEvents: 'none'` in LabeledEdge).
 *
 * Deshalb pinnen die Tests hier für jeden Marker beide Richtungen.
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
  /** So sieht eine Edge aus, nachdem ein Undo den projizierten Snapshot zurückgeschrieben hat. */
  const staleEdge: Edge = { ...cleanEdge, data: { label: 'On Success', __detached: true } };

  it('detachedEdgeId_marksThatEdge', () => {
    const { displayedEdges } = project({ edges: [cleanEdge], detachedEdgeId: 'e1' });
    expect(edgeData(displayedEdges, 'e1').__detached).toBe(true);
  });

  it('noDetachInProgress_stripsAStaleMarkerFromRawEdges', () => {
    // DIE Regression: ohne diesen Aufräum-Zweig bleibt die Edge nach einem Undo gedimmt
    // und `pointerEvents: 'none'` — nicht mehr anklickbar bis zum Reload.
    const { displayedEdges } = project({ edges: [staleEdge], detachedEdgeId: null });
    expect(edgeData(displayedEdges, 'e1')).not.toHaveProperty('__detached');
    expect(edgeData(displayedEdges, 'e1').label).toBe('On Success'); // Rest bleibt unangetastet
  });

  it('anotherEdgeDetached_stillStripsTheStaleMarker', () => {
    const other: Edge = { id: 'e2', source: 'b', target: 'a', type: 'labeled', data: {} };
    const { displayedEdges } = project({ edges: [staleEdge, other], detachedEdgeId: 'e2' });

    expect(edgeData(displayedEdges, 'e1')).not.toHaveProperty('__detached');
    expect(edgeData(displayedEdges, 'e2').__detached).toBe(true);
  });

  it('cleanEdgeWithoutDetach_isPassedThroughUntouched', () => {
    // Kein unnötiges Neu-Objekt: die Identitätsprüfung der Projektion muss greifen, sonst
    // rendert jede Edge bei jedem Pass neu.
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
    // Gleiche Falle wie oben: der Ring darf nach einem Undo nicht an einem Node kleben.
    const stale: Node[] = NODES.map((n) => ({ ...n, className: 'np-dock-target' }));
    const { displayedNodes } = project({ nodes: stale, dockTargetNodeId: null });
    for (const n of displayedNodes) expect(n.className).toBeUndefined();
  });
});
