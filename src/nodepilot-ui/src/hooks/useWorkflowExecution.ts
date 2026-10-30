import { useState, useCallback, useEffect } from 'react';
import { useAsyncIdentity } from './useAsyncIdentity';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../api/client';
import { getPage } from '../api/paging';
import { cancelExecution } from '../api/operations';
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
  const captureOwner = useAsyncIdentity(workflowId);
  useEffect(() => { setShowRunDialog(false); setPendingRunIsDebug(false); }, [workflowId]);

  // Most recent execution, fetched only when the run dialog is about to open, to pre-fill the
  // parameter form with the values used last time. Genuinely bounded to one row: the endpoint
  // pages with `pageSize` (`limit` was ignored, so it returned the default 100 rows, each
  // carrying its input parameters and return data), and it answers with a PagedResponse whose
  // `items` hold the rows.
  const { data: lastExecutionPage } = useQuery({
    queryKey: ['last-execution', workflowId],
    queryFn: () => getPage<{ id: string; inputParametersJson: string | null }>(
      `/executions?workflowId=${workflowId}`, 1, 1),
    enabled: !!workflowId && showRunDialog,
    staleTime: 30_000,
  });
  const lastExecutionList = lastExecutionPage?.items;

  const executeMutation = useMutation({
    mutationFn: (args: { workflowId: string; current: () => boolean; params?: Record<string, string>; debug?: boolean }) =>
      withSpan(
        'designer.execute',
        () => api.post<WorkflowExecution>(`/workflows/${args.workflowId}/execute`, {
          parameters: args?.params,
          debug: args?.debug ?? false,
        }),
        {
          'nodepilot.workflow.id': args.workflowId,
          'nodepilot.designer.has_parameters': !!args?.params,
          'nodepilot.designer.debug': args?.debug ?? false,
        },
      ),
    onSuccess: (execution, request) => {
      if (!request.current()) return;
      pinCanvasExecution(execution.id);
      clearReplay();
    },
    // Every call site passes its own onError to mutate().
    meta: { handlesError: true },
  });

  // Test/Debug entry point. `debug` applies to this one run only and makes the engine honor
  // breakpoints. Memoized so the keyboard handlers that close over `run` keep a stable
  // reference instead of re-creating every render or capturing stale props.
  const run = useCallback(async (debug = false) => {
    if (!workflow || !workflowId) return;
    const current = captureOwner();
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
      if (!current() || !isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    }

    // Route to the parameter dialog when the workflow has a manual trigger with parameters.
    // A published run executes the saved definition. The canvas can still be hydrating
    // when the toolbar is first clicked; an empty canvas must not bypass its parameters.
    const triggerConfig = extractManualTriggerConfig(canWrite ? JSON.stringify({ nodes, edges }) : workflow.definitionJson);
    if (triggerConfig && triggerConfig.parameters.length > 0) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setShowRunDialog(true);
      setPendingRunIsDebug(debug);
    } else {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      executeMutation.mutate({ debug, workflowId, current }, {
        onError: (err) => toast.error(t('editor:executionStartFailed', { message: (err as Error).message })),
      });
    }
  }, [workflow, workflowId, captureOwner, t, canWrite, isDirty, saveAsync, nodes, edges, executeMutation]);

  /** Submits the parameter dialog: starts the run with the collected params and debug flag. */
  const confirmRunWithParams = useCallback((params: Record<string, string>) => {
    if (!workflowId) return;
    executeMutation.mutate({ params, debug: pendingRunIsDebug, workflowId, current: captureOwner() }, {
      onError: (err) => toast.error(t('editor:executionStartFailed', { message: (err as Error).message })),
    });
    setShowRunDialog(false);
    setPendingRunIsDebug(false);
  }, [executeMutation, pendingRunIsDebug, workflowId, captureOwner, t]);
  const closeRunDialog = useCallback(() => { setShowRunDialog(false); }, []);

  // No onError of its own: the global mutation toast reports a failed cancel with the API message.
  const { mutate: cancel } = useMutation({ mutationFn: (executionId: string) => cancelExecution(executionId) });

  return {
    run,
    confirmRunWithParams,
    closeRunDialog,
    showRunDialog,
    lastExecutionList,
    cancel,
  };
}
