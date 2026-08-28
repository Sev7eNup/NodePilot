import { useState, useCallback } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../api/client';
import type { Workflow, WorkflowExecution } from '../types/api';
import { withSpan } from '../telemetry/otel';
import { extractManualTriggerConfig } from '../components/common/RunWorkflowDialog';
import { toast } from '../stores/toastStore';
import {
  captureAuthBoundaryGeneration,
  isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';

interface UseWorkflowExecutionArgs {
  workflowId: string | undefined;
  workflow: Workflow | undefined;
  /** Whether the user holds the edit-lock (drives save-before-run). */
  canWrite: boolean;
  isDirty: boolean;
  nodes: Node[];
  edges: Edge[];
  /** Awaitable save from useWorkflowPersistence; a failed save aborts the run. */
  saveAsync: () => Promise<unknown>;
  /** Pin the started execution's path-coloring onto the canvas (useCanvasExecutionState). */
  pinCanvasExecution: (executionId: string) => void;
  clearReplay: () => void;
}

/**
 * Owns running a workflow from the designer. The execute mutation, the run-with-parameters
 * dialog state and the last-execution prefill query stay internal; only intent commands are
 * exposed. `run(debug)` saves first when dirty and opens the parameter dialog when the manual
 * trigger declares parameters, `confirmRunWithParams` submits that dialog, and
 * `closeRunDialog` closes it.
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

  // Most recent execution, fetched only when the run dialog is about to open, to pre-fill the
  // parameter form with the values used last time. Bounded to one row to keep the call cheap.
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

  // Test/Debug entry point. `debug` applies to this one run only and makes the engine honor
  // breakpoints. Memoized so the keyboard handlers that close over `run` keep a stable
  // reference instead of re-creating every render or capturing stale props.
  const run = useCallback(async (debug = false) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    if (workflow && !workflow.isEnabled) {
      toast.info(t('editor:workflowDisabledRunHint'));
      return;
    }
    // Save only while the user holds the edit lock and has unsaved changes. On a published or
    // read-only workflow the PUT would answer 423 Locked, while running stays allowed because
    // /execute has no lock check.
    if (canWrite && isDirty) {
      try {
        await saveAsync();
      } catch (err) {
        void err;
        return;
      }
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    }

    // Route to the parameter dialog when the workflow has a manual trigger with parameters.
    const triggerConfig = extractManualTriggerConfig(JSON.stringify({ nodes, edges }));
    if (triggerConfig && triggerConfig.parameters.length > 0) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setShowRunDialog(true);
      setPendingRunIsDebug(debug);
    } else {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      executeMutation.mutate({ debug }, {
        onError: (err) => toast.error(t('editor:executionStartFailed', { message: (err as Error).message })),
      });
    }
  }, [workflow, t, canWrite, isDirty, saveAsync, nodes, edges, executeMutation]);

  /** Submits the parameter dialog: starts the run with the collected params and debug flag. */
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
