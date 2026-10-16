import { useMemo } from 'react';
import type { Node } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { WorkflowCoverageResponse, NodeCoverageStats } from '../types/api';

/**
 * Coverage classification that drives the canvas tint while the heatmap toggle is on.
 *  - never: no executions in the window; grey dashed border
 *  - rare: fewer than 25% of the executions reached this node; faint amber
 *  - common: 25% or more reached this node; no tint
 *  - failure: reached but failed at least once; red corner dot, drawn in addition
 *
 * The threshold is deliberately coarse. Step stats carry the fine-grained distribution.
 */
export type CoverageClass = 'never' | 'rare' | 'common';

export interface CoverageAnnotation {
  cls: CoverageClass;
  executedCount: number;
  failedCount: number;
  skippedCount: number;
  totalExecutions: number;
  /** Number of days the data covers; matches what the API was asked for. */
  windowDays: number;
  lastExecutedAt: string | null;
}

/**
 * Fetches coverage stats while the heatmap toggle is on and derives a `__coverage` annotation per
 * activity node, which useDisplayedGraph merges into the projection. Returns null while the toggle
 * is off or the data is still loading, so the default look shows.
 */
export function useCoverageHeatmap({
  workflowId,
  enabled,
  windowDays,
  nodes,
}: {
  workflowId: string | undefined;
  enabled: boolean;
  windowDays: number;
  nodes: Node[];
}): ReadonlyMap<string, CoverageAnnotation> | null {
  const { data: coverage } = useQuery({
    queryKey: ['workflow-coverage', workflowId, windowDays],
    enabled: !!workflowId && enabled,
    staleTime: 60_000,
    queryFn: () => api.get<WorkflowCoverageResponse>(
      `/workflows/${workflowId}/coverage?windowDays=${windowDays}`,
    ),
  });

  // Keyed on the activity node ids rather than `nodes`, which changes on every frame of a drag.
  // A move keeps the key, so the annotation objects stay the same and node memos hold.
  const activityIdsKey = enabled
    ? JSON.stringify(nodes.filter((n) => n.type === 'activity').map((n) => n.id))
    : null;

  return useMemo(() => {
    if (activityIdsKey === null || !coverage) return null;
    const byStepId = new Map<string, NodeCoverageStats>();
    for (const s of coverage.nodes) byStepId.set(s.stepId, s);

    const total = coverage.totalExecutions;
    const byNode = new Map<string, CoverageAnnotation>();
    for (const nodeId of JSON.parse(activityIdsKey) as string[]) {
      const stats = byStepId.get(nodeId);
      const executed = stats?.executedCount ?? 0;
      const cls: CoverageClass =
        executed === 0 ? 'never'
        : (total > 0 && executed / total < 0.25) ? 'rare'
        : 'common';
      byNode.set(nodeId, {
        cls,
        executedCount: executed,
        failedCount: stats?.failedCount ?? 0,
        skippedCount: stats?.skippedCount ?? 0,
        totalExecutions: total,
        windowDays: coverage.windowDays,
        lastExecutedAt: stats?.lastExecutedAt ?? null,
      });
    }
    return byNode;
  }, [activityIdsKey, coverage]);
}
