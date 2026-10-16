import { useMemo } from 'react';
import type { Node, Edge } from '@xyflow/react';
import type { LintResult } from '../lib/workflowLint';
import type { SimulationResult } from './useWorkflowSimulation';
import type { CriticalPathAnnotation } from './useCriticalPath';
import type { NodeAnnotation } from './useNodeAnnotations';
import type { CoverageAnnotation } from './useCoverageHeatmap';
import { computeFlowingVariablesPerEdge } from '../lib/variableUsageScan';
import { withDefaultEdgePorts } from '../lib/edgePorts';
import { buildCollapsedGraphView } from '../lib/collapsedGraphView';

/** Stable empties, so a disabled overlay or filter never invalidates a memo that depends on it. */
const EMPTY_FLOWING_VARS: ReadonlyMap<string, string[]> = new Map<string, string[]>();
const EMPTY_HIDDEN_NODE_IDS: ReadonlySet<string> = new Set<string>();
const NO_ANNOTATION: NodeAnnotation = Object.freeze({});

/**
 * Node data keys this projection owns, including the runtime annotations it merges in. They are
 * dropped from the raw data before patching, so a value that leaked into the raw graph is never
 * shown as if it were current.
 */
function withoutProjectionKeys(data: Record<string, unknown>): Record<string, unknown> {
  const {
    __lintErrors: _lintErrors, __lintWarnings: _lintWarnings, __simulated: _simulated,
    __failureTint: _failureTint, __criticalPath: _criticalPath, __coverage: _coverage,
    __liveStatus: _liveStatus, __health: _health, __stats: _stats, __machineColorIdx: _machineColorIdx,
    __varFlowRole: _varFlowRole, __workflowEnabled: _workflowEnabled, ...authored
  } = data;
  return authored;
}

/** Drops one projection-owned key from edge data, keeping the object when the key is absent. */
function withoutEdgeKey(data: Record<string, unknown>, key: string): Record<string, unknown> {
  if (data[key] === undefined) return data;
  const { [key]: _omit, ...rest } = data;
  return rest;
}

interface UseDisplayedGraphArgs {
  nodes: Node[];
  edges: Edge[];
  edgesAnimated: boolean;
  hiddenActivityTypes: Set<string>;
  dataFlowOverlayEnabled: boolean;
  simulation: SimulationResult | null;
  revealIndex: number;
  lintResult: LintResult;
  failureHeatmapEnabled: boolean;
  /** Critical-path overlay from useCriticalPath; null while the overlay is off. */
  criticalPath: ReadonlyMap<string, CriticalPathAnnotation> | null;
  /** Runtime annotations per node id from useNodeAnnotations. */
  annotations: ReadonlyMap<string, NodeAnnotation>;
  /** Coverage heatmap from useCoverageHeatmap; null while the heatmap is off. */
  coverage: ReadonlyMap<string, CoverageAnnotation> | null;
  /** Edges on the hovered variable's path, from useNodeAnnotations. */
  varFlowEdgeIds: ReadonlySet<string>;
  /** Edge whose target end is currently detached (context menu -> "Detach target"). */
  detachedEdgeId?: string | null;
  /** Node the detach preview is currently docking to — gets the highlight ring. */
  dockTargetNodeId?: string | null;
}

/**
 * Read-only projection of the raw nodes/edges into what the React Flow canvas actually
 * renders: in-degree badges, simulation reveal state, lint counts, failure tint, critical path,
 * runtime annotations (live status, health, stats, machine colour, coverage, variable hover),
 * the activity-type filter, edge ports + animation + variable-flow overlay, and finally the
 * group-collapse view. Wide input (it reads every annotation/toggle the canvas cares
 * about), deliberately narrow output — the page only consumes `{ displayedNodes,
 * displayedEdges }`. The intermediate memos (flowingVarsPerEdge, the patched edge list,
 * the per-node data patch) stay private to this hook.
 */
export function useDisplayedGraph({
  nodes,
  edges,
  edgesAnimated,
  hiddenActivityTypes,
  dataFlowOverlayEnabled,
  simulation,
  revealIndex,
  lintResult,
  failureHeatmapEnabled,
  criticalPath,
  annotations,
  coverage,
  varFlowEdgeIds,
  detachedEdgeId = null,
  dockTargetNodeId = null,
}: UseDisplayedGraphArgs): { displayedNodes: Node[]; displayedEdges: Edge[] } {
  // Variable-flow overlay: only computed when the toggle is on (the analysis touches every
  // node's config payload + transitive successor sets, so we don't pay for it during normal
  // editing). Key = edge id, value = sorted heads that flow across that edge.
  // With the toggle off this must stay the same empty map, not a fresh one — it is a
  // dependency of the edge projection below, which would otherwise rebuild every edge on
  // every render.
  const flowingVarsPerEdge = useMemo(() => {
    if (!dataFlowOverlayEnabled) return EMPTY_FLOWING_VARS;
    return computeFlowingVariablesPerEdge(nodes, edges);
  }, [dataFlowOverlayEnabled, nodes, edges]);

  // Nodes removed by the activity-type filter. Split out of the edge projection so that
  // projection depends on this set rather than on `nodes`: with no filter active the set is
  // a constant, and dragging a node then leaves every edge object untouched.
  const hiddenNodeIds = useMemo(() => {
    if (hiddenActivityTypes.size === 0) return EMPTY_HIDDEN_NODE_IDS;
    const ids = new Set<string>();
    for (const n of nodes) {
      if (n.type !== 'activity') continue;
      const activityType = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      if (activityType && hiddenActivityTypes.has(activityType)) ids.add(n.id);
    }
    return ids;
  }, [nodes, hiddenActivityTypes]);

  const displayedEdges = useMemo(() => {
    // Edges that touch a filter-hidden node get `hidden: true` too, otherwise they
    // dangle as orphan arrows pointing to nothing.
    return edges.map((e) => {
      const shouldHide = hiddenNodeIds.has(e.source) || hiddenNodeIds.has(e.target);
      const withPorts = withDefaultEdgePorts(e);
      const flowingVars = flowingVarsPerEdge.get(e.id);
      // Patch into edge.data so LabeledEdge can read it without an extra prop drill. We
      // overwrite/clear regardless of whether the toggle is currently on — switching off
      // must remove the visual immediately, not stick around from the last computation.
      const baseData = (withPorts.data as Record<string, unknown> | undefined) ?? {};
      const dataWithFlow = dataFlowOverlayEnabled
        ? { ...baseData, __flowingVars: flowingVars ?? [] }
        : baseData.__flowingVars !== undefined
          ? (() => { const { __flowingVars: _, ...rest } = baseData as Record<string, unknown>; return rest; })()
          : baseData;
      // Always remove projected __detached state from raw edges. Otherwise undo can persist the
      // marker and leave the restored edge dimmed and non-interactive.
      const isDetached = e.id === detachedEdgeId;
      const dataWithDetach = isDetached
        ? { ...dataWithFlow, __detached: true }
        : withoutEdgeKey(dataWithFlow, '__detached');
      const finalData = varFlowEdgeIds.has(e.id)
        ? { ...dataWithDetach, __varFlowHighlighted: true }
        : withoutEdgeKey(dataWithDetach, '__varFlowHighlighted');
      // Keep the edge object when the projection changes nothing about it. `animated` is
      // coerced on both sides because an edge hydrated from definitionJson carries no
      // `animated` at all, and a strict comparison against the toggle could never hold.
      if (
        withPorts === e
        && !!e.animated === edgesAnimated
        && !!e.hidden === shouldHide
        && finalData === baseData
      ) return e;
      return { ...withPorts, animated: edgesAnimated, hidden: shouldHide, data: finalData };
    });
  }, [edges, edgesAnimated, hiddenNodeIds, flowingVarsPerEdge, dataFlowOverlayEnabled, detachedEdgeId, varFlowEdgeIds]);

  // Lint counts per node, bucketed once. Scanning the issue lists inside the node loop below
  // made the projection quadratic in graph size.
  const lintCountsByNode = useMemo(() => {
    const counts = new Map<string, { errors: number; warnings: number }>();
    const bump = (nodeId: string | undefined, key: 'errors' | 'warnings') => {
      if (!nodeId) return;
      const entry = counts.get(nodeId) ?? { errors: 0, warnings: 0 };
      entry[key] += 1;
      counts.set(nodeId, entry);
    };
    for (const issue of lintResult.errors) bump(issue.nodeId, 'errors');
    for (const issue of lintResult.warnings) bump(issue.nodeId, 'warnings');
    return counts;
  }, [lintResult]);

  // Compute in-degree per node for Junction mode and invalid direct fan-in badges.
  const nodesWithDegree = useMemo(() => {
    const inDegree = new Map<string, number>();
    for (const e of edges) {
      const active = !((e.data as Record<string, unknown>)?.disabled);
      if (active) inDegree.set(e.target, (inDegree.get(e.target) ?? 0) + 1);
    }
    // Reveal animation: a node is only marked 'reachable' once its index in
    // simulation.order is <= revealIndex. Before that it stays neutral (no __simulated),
    // so it looks the same as in edit mode. Skipped nodes only get their badge once
    // revealIndex reaches the end — otherwise they'd turn grey from the very first click,
    // before the simulated "execution" has even reached their level, breaking the
    // step-by-step animation metaphor.
    const revealComplete = simulation ? revealIndex >= simulation.order.length : true;
    const orderIndex = simulation
      ? new Map(simulation.order.map((id, i) => [id, i]))
      : null;
    return nodes.map((n) => {
      const annotation = annotations.get(n.id) ?? NO_ANNOTATION;
      const dataPatch: Record<string, unknown> = {
        ...withoutProjectionKeys(n.data as Record<string, unknown>),
        ...annotation,
        inDegreeCount: inDegree.get(n.id) ?? 0,
      };
      const coverageAnnotation = coverage?.get(n.id);
      if (coverageAnnotation) dataPatch.__coverage = coverageAnnotation;
      if (simulation && orderIndex) {
        if (simulation.reachable.has(n.id)) {
          const idx = orderIndex.get(n.id) ?? 0;
          if (idx < revealIndex) dataPatch.__simulated = 'reachable';
          else if (idx === revealIndex) dataPatch.__simulated = 'revealing'; // the step currently being animated
        } else if (simulation.skipped.has(n.id) && revealComplete) {
          dataPatch.__simulated = 'skipped';
        }
      }
      if (!annotation.__liveStatus) {
        const counts = lintCountsByNode.get(n.id);
        if (counts) {
          if (counts.errors > 0) dataPatch.__lintErrors = counts.errors;
          if (counts.warnings > 0) dataPatch.__lintWarnings = counts.warnings;
        }
      }
      // Activity-type filter — `hidden` removes the node from the canvas without deleting it.
      // `displayedEdges` mirrors this by hiding any edge that touches a filtered-out node.
      const activityType = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      const isHidden = n.type === 'activity' && activityType ? hiddenActivityTypes.has(activityType) : false;
      // Failure heatmap: when toggle is on, surface the failure rate so ActivityNode can
      // render a red-shade border. We piggyback on the __stats data fetched separately.
      if (failureHeatmapEnabled) {
        const stats = annotation.__stats;
        if (stats && stats.totalRuns > 0 && stats.failureRate > 0) dataPatch.__failureTint = stats.failureRate;
      }
      const criticalPathAnnotation = criticalPath?.get(n.id);
      if (criticalPathAnnotation) dataPatch.__criticalPath = criticalPathAnnotation;
      // Mark only the accepted preview target so the ring never promises an invalid connection.
      const isDockTarget = n.id === dockTargetNodeId;
      return { ...n, data: dataPatch, hidden: isHidden, className: isDockTarget ? 'np-dock-target' : undefined };
    });
  }, [nodes, edges, simulation, revealIndex, lintCountsByNode, hiddenActivityTypes, failureHeatmapEnabled,
    criticalPath, annotations, coverage, dockTargetNodeId]);

  const collapsedGraphView = useMemo(
    () => buildCollapsedGraphView(nodesWithDegree, displayedEdges),
    [nodesWithDegree, displayedEdges],
  );

  return { displayedNodes: collapsedGraphView.nodes, displayedEdges: collapsedGraphView.edges };
}
