import { useMemo } from 'react';
import type { Node, Edge } from '@xyflow/react';
import type { LintResult } from '../lib/workflowLint';
import type { SimulationResult } from './useWorkflowSimulation';
import { computeFlowingVariablesPerEdge } from '../lib/variableUsageScan';
import { withDefaultEdgePorts } from '../lib/edgePorts';
import { buildCollapsedGraphView } from '../lib/collapsedGraphView';

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
}

/**
 * Read-only projection of the raw nodes/edges into what the React Flow canvas actually
 * renders: in-degree badges, simulation reveal state, lint counts, failure tint, the
 * activity-type filter, edge ports + animation + variable-flow overlay, and finally the
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
}: UseDisplayedGraphArgs): { displayedNodes: Node[]; displayedEdges: Edge[] } {
  // Variable-flow overlay: only computed when the toggle is on (the analysis touches every
  // node's config payload + transitive successor sets, so we don't pay for it during normal
  // editing). Key = edge id, value = sorted heads that flow across that edge.
  const flowingVarsPerEdge = useMemo(() => {
    if (!dataFlowOverlayEnabled) return new Map<string, string[]>();
    return computeFlowingVariablesPerEdge(nodes, edges);
  }, [dataFlowOverlayEnabled, nodes, edges]);

  const displayedEdges = useMemo(() => {
    // Edges that touch a filter-hidden node get `hidden: true` too, otherwise they
    // dangle as orphan arrows pointing to nothing.
    const hiddenNodeIds = new Set<string>();
    if (hiddenActivityTypes.size > 0) {
      for (const n of nodes) {
        if (n.type !== 'activity') continue;
        const activityType = (n.data as Record<string, unknown>)?.activityType as string | undefined;
        if (activityType && hiddenActivityTypes.has(activityType)) hiddenNodeIds.add(n.id);
      }
    }
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
      if (
        withPorts === e
        && e.animated === edgesAnimated
        && !!e.hidden === shouldHide
        && dataWithFlow === baseData
      ) return e;
      return { ...withPorts, animated: edgesAnimated, hidden: shouldHide, data: dataWithFlow };
    });
  }, [edges, edgesAnimated, nodes, hiddenActivityTypes, flowingVarsPerEdge, dataFlowOverlayEnabled]);

  // Compute in-degree per node for the fan-in badge (automatic Junction indicator)
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
      const dataPatch: Record<string, unknown> = { ...n.data, inDegreeCount: inDegree.get(n.id) ?? 0 };
      if (simulation && orderIndex) {
        if (simulation.reachable.has(n.id)) {
          const idx = orderIndex.get(n.id) ?? 0;
          if (idx < revealIndex) dataPatch.__simulated = 'reachable';
          else if (idx === revealIndex) dataPatch.__simulated = 'revealing'; // the step currently being animated
        } else if (simulation.skipped.has(n.id) && revealComplete) {
          dataPatch.__simulated = 'skipped';
        }
      }
      const hasLiveStatus = !!(n.data as Record<string, unknown>).__liveStatus;
      if (!hasLiveStatus) {
        const errCount = lintResult.errors.filter((i) => i.nodeId === n.id).length;
        const warnCount = lintResult.warnings.filter((i) => i.nodeId === n.id).length;
        if (errCount > 0) dataPatch.__lintErrors = errCount;
        if (warnCount > 0) dataPatch.__lintWarnings = warnCount;
      }
      // Activity-type filter — `hidden` removes the node from the canvas without deleting it.
      // `displayedEdges` mirrors this by hiding any edge that touches a filtered-out node.
      const activityType = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      const isHidden = n.type === 'activity' && activityType ? hiddenActivityTypes.has(activityType) : false;
      // Failure heatmap: when toggle is on, surface the failure rate so ActivityNode can
      // render a red-shade border. We piggyback on the __stats data fetched separately.
      if (failureHeatmapEnabled) {
        const stats = (n.data as Record<string, unknown>)?.__stats as { failureRate: number; totalRuns: number } | undefined;
        if (stats && stats.totalRuns > 0 && stats.failureRate > 0) dataPatch.__failureTint = stats.failureRate;
      }
      return { ...n, data: dataPatch, hidden: isHidden };
    });
  }, [nodes, edges, simulation, revealIndex, lintResult, hiddenActivityTypes, failureHeatmapEnabled]);

  const collapsedGraphView = useMemo(
    () => buildCollapsedGraphView(nodesWithDegree, displayedEdges),
    [nodesWithDegree, displayedEdges],
  );

  return { displayedNodes: collapsedGraphView.nodes, displayedEdges: collapsedGraphView.edges };
}
