import { useState, useCallback } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../api/client';
import type { Workflow, WorkflowExecution } from '../types/api';
import { withSpan } from '../telemetry/otel';
import { extractManualTriggerConfig } from '../components/common/RunWorkflowDialog';
import { toast } from '../stores/toastStore';

interface UseWorkflowExecutionArgs {
  workflowId: string | undefined;
  workflow: Workflow | undefined;
  /** Whether the user holds the edit-lock (drives save-before-run). */
  canWrite: boolean;
  isDirty: boolean;
  nodes: Node[];
  edges: Edge[];
  /** Awaitable save from useWorkflowPersistence — a failed save aborts the run. */
  saveAsync: () => Promise<unknown>;
  /** Pin the started execution's path-coloring onto the canvas (useCanvasExecutionState). */
  pinCanvasExecution: (executionId: string) => void;
  clearReplay: () => void;
}

/**
 * Owns running a workflow from the designer: the execute mutation, the run-with-parameters
 * dialog state (open flag + pending debug flag), and the last-execution prefill query — all
 * internal. Exposes intent commands only: `run(debug)` (the Test/Debug entry point, which
 * saves first when dirty and routes to the param dialog when the manual trigger declares
 * parameters), `confirmRunWithParams` (the dialog's submit), and `closeRunDialog`. The raw
 * mutation and the dialog setters never leak out.
 */
export function useWorkflowExecution({
  workflowId,
  workflow,
  canWrite,
  isDirty,
  nodes,
  edges,
  saveAsync,
  pinCanvasExecution,
  clearReplay,
}: UseWorkflowExecutionArgs) {
  const { t } = useTranslation(['editor']);
  const [showRunDialog, setShowRunDialog] = useState(false);
  const [pendingRunIsDebug, setPendingRunIsDebug] = useState(false);

  // Most recent execution — fetched lazily when the run dialog is about to open so we can
  // pre-fill the parameter form with whatever was used last time. Bounded to 1 row to keep
  // the network call cheap.
  const { data: lastExecutionList } = useQuery({
    queryKey: ['last-execution', workflowId],
    queryFn: () => api.get<Array<{ id: string; inputParametersJson: string | null }>>(`/executions?workflowId=${workflowId}&limit=1`),
    enabled: !!workflowId && showRunDialog,
    staleTime: 30_000,
  });

  const executeMutation = useMutation({
    mutationFn: (args?: { params?: Record<string, string>; debug?: boolean }) =>
      withSpan(
        'designer.execute',
        () => api.post<WorkflowExecution>(`/workflows/${workflowId}/execute`, {
          parameters: args?.params,
          debug: args?.debug ?? false,
        }),
        {
          'nodepilot.workflow.id': workflowId ?? 'unknown',
          'nodepilot.designer.has_parameters': !!args?.params,
          'nodepilot.designer.debug': args?.debug ?? false,
        },
      ),
    onSuccess: (execution) => {
      pinCanvasExecution(execution.id);
      clearReplay();
    },
  });

  // Test/Debug entry point. When `debug` is true, the execution starts with `debug: true`
  // so the engine honors breakpoints. This isn't a persistent flag — it only applies to
  // this one run.
  // Memoized so the keyboard handlers (triggerTest/triggerDebug) that close over `run` can
  // depend on it honestly — an unmemoized `run` would force those handlers to either re-create
  // every render or capture a stale closure (stale isDirty/nodes/edges/saveAsync).
  const run = useCallback(async (debug = false) => {
    if (workflow && !workflow.isEnabled) {
      toast.info(t('editor:workflowDisabledRunHint'));
      return;
    }
    // Only save if the user holds the edit lock AND there are unsaved changes. On a
    // published/read-only workflow the PUT would otherwise fail with 423 Locked — but
    // running/testing is still allowed, since /execute itself has no lock check.
    if (canWrite && isDirty) {
      try {
        await saveAsync();
      } catch (err) {
        void err;
        return;
      }
    }

    // Check if workflow has a ManualTrigger node with parameters.
    const triggerConfig = extractManualTriggerConfig(JSON.stringify({ nodes, edges }));
    if (triggerConfig && triggerConfig.parameters.length > 0) {
      setShowRunDialog(true);
      setPendingRunIsDebug(debug);
    } else {
      executeMutation.mutate({ debug }, {
        onError: (err) => toast.error(t('editor:executionStartFailed', { message: (err as Error).message })),
      });
    }
  }, [workflow, t, canWrite, isDirty, saveAsync, nodes, edges, executeMutation]);

  /** Submit the parameter dialog — fires the run with the collected params + pending debug flag. */
  const confirmRunWithParams = useCallback((params: Record<string, string>) => {
    executeMutation.mutate({ params, debug: pendingRunIsDebug }, {
      onError: (err) => toast.error(t('editor:executionStartFailed', { message: (err as Error).message })),
    });
    setShowRunDialog(false);
    setPendingRunIsDebug(false);
  }, [executeMutation, pendingRunIsDebug, t]);
  const closeRunDialog = useCallback(() => { setShowRunDialog(false); }, []);

  return {
    run,
    confirmRunWithParams,
    closeRunDialog,
    showRunDialog,
    lastExecutionList,
  };
}
