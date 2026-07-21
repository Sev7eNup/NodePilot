import { useEffect } from 'react';
import type { Node } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { WorkflowCoverageResponse, NodeCoverageStats } from '../types/api';

/**
 * Coverage classification — drives the canvas tint when the heatmap toggle is on.
 *  - never:     node has no executions in the window AT ALL → grey/dashed border
 *  - rare:      < 25% of total executions reached this node → faint amber
 *  - common:    >= 25% reached this node → no tint (normal)
 *  - failure:   reached but failed at least once → red dot in corner (additive)
 *
 * Threshold is intentionally crude — the V1 goal is "show me what's never run", not
 * a precise gradient. Operators looking for fine-grained distribution use step-stats.
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
 * Fetches coverage stats and stamps `__coverage` onto each activity node so
 * ActivityNode.tsx can render a tint without prop-drilling. Cleared when the toggle
 * is off so a stale grey border doesn't bleed into normal designer use.
 */
export function useCoverageHeatmap({
  workflowId,
  enabled,
  windowDays,
  setNodes,
}: {
  workflowId: string | undefined;
  enabled: boolean;
  windowDays: number;
  setNodes: React.Dispatch<React.SetStateAction<Node[]>>;
}) {
  const { data: coverage } = useQuery({
    queryKey: ['workflow-coverage', workflowId, windowDays],
    enabled: !!workflowId && enabled,
    staleTime: 60_000,
    queryFn: () => api.get<WorkflowCoverageResponse>(
      `/workflows/${workflowId}/coverage?windowDays=${windowDays}`,
    ),
  });

  useEffect(() => {
    if (!enabled || !coverage) {
      // Clear stale annotations so toggling off restores the default look.
      setNodes((nds) => nds.map((n) => {
        const d = n.data as Record<string, unknown>;
        if (!('__coverage' in d)) return n;
        const { __coverage: _c, ...rest } = d;
        void _c;
        return { ...n, data: rest };
      }));
      return;
    }

    const byStepId = new Map<string, NodeCoverageStats>();
    for (const s of coverage.nodes) byStepId.set(s.stepId, s);

    const total = coverage.totalExecutions;
    setNodes((nds) => nds.map((n) => {
      if (n.type !== 'activity') return n;
      const stats = byStepId.get(n.id);
      const executed = stats?.executedCount ?? 0;
      const cls: CoverageClass =
        executed === 0 ? 'never'
        : (total > 0 && executed / total < 0.25) ? 'rare'
        : 'common';
      const annotation: CoverageAnnotation = {
        cls,
        executedCount: executed,
        failedCount: stats?.failedCount ?? 0,
        skippedCount: stats?.skippedCount ?? 0,
        totalExecutions: total,
        windowDays: coverage.windowDays,
        lastExecutedAt: stats?.lastExecutedAt ?? null,
      };
      const current = (n.data as Record<string, unknown>).__coverage as CoverageAnnotation | undefined;
      if (current?.cls === annotation.cls
        && current.executedCount === annotation.executedCount
        && current.failedCount === annotation.failedCount
        && current.totalExecutions === annotation.totalExecutions
      ) return n;
      return { ...n, data: { ...n.data, __coverage: annotation } };
    }));
  }, [coverage, enabled, setNodes]);
}
