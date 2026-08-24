import { useCallback, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import type { Workflow } from '../types/api';
import { api } from '../api/client';
import { sharedFoldersApi, ROOT_FOLDER_ID } from '../api/sharedFolders';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import { downloadTextFile } from '../lib/chatExport';
import {
  runBulkOperation, BulkSkippedError, type BulkProgress, type BulkResult,
} from '../lib/bulkOperations';
import {
  AuthBoundaryChangedError,
  captureAuthBoundaryGeneration,
  isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';

/** Minimal view of a v1 export envelope — only the fields the bulk export re-packs. */
export type WorkflowExportEnvelope = {
  schema: string;
  exportVersion: number;
  exportedAt: string;
  workflow?: unknown;
  workflows?: unknown[];
};

export interface WorkflowBulkActions {
  progress: BulkProgress | null;
  running: boolean;
  abortRequested: boolean;
  requestAbort: () => void;
  deleteWorkflows: (items: Workflow[]) => Promise<void>;
  moveWorkflows: (items: Workflow[], targetFolderId: string) => Promise<void>;
  setEnabled: (items: Workflow[], enable: boolean) => Promise<void>;
  exportWorkflows: (items: Workflow[]) => Promise<void>;
}

/**
 * The four bulk actions of the workflow list, as a hook rather than as part of the action bar —
 * the drag-and-drop path needs `moveWorkflows` too, and it fires from the folder tree's drop
 * handler in WorkflowsPage, not from a button in the bar.
 *
 * Every action is a sequential loop over the SAME single-workflow endpoints the per-row buttons
 * use. There is no bulk endpoint, deliberately: each iteration keeps its own RBAC check, its own
 * edit-lock check and its own audit row, so a bulk run can never become a way around a permission
 * that the single action would have refused.
 */
export function useWorkflowBulkActions(
  onRetain: (failedIds: string[]) => void,
): WorkflowBulkActions {
  const { t } = useTranslation(['workflows', 'common']);
  const queryClient = useQueryClient();
  const [progress, setProgress] = useState<BulkProgress | null>(null);
  const [abortRequested, setAbortRequested] = useState(false);
  // The running loop closed over this ref when it started; React state would be a stale snapshot
  // inside it, so Cancel could never reach the loop it is meant to stop.
  const abortRef = useRef(false);

  const report = useCallback((
    result: BulkResult<Workflow>,
    total: number,
    successKey: string,
    invalidateFolders: boolean,
  ) => {
    queryClient.invalidateQueries({ queryKey: ['workflows'] });
    if (invalidateFolders) queryClient.invalidateQueries({ queryKey: ['shared-folders'] });

    const lines: string[] = [];
    if (result.aborted) {
      lines.push(t('workflows:bulk.cancelled', { done: result.succeeded.length, total }));
    } else {
      lines.push(t(successKey, { count: result.succeeded.length }));
    }
    if (result.skipped.length > 0) lines.push(t('workflows:bulk.skipped', { count: result.skipped.length }));

    if (result.failed.length > 0) {
      lines.push('', t('workflows:bulk.failedHeader', { count: result.failed.length }));
      for (const f of result.failed) lines.push(`✗ ${f.item.name}: ${f.message}`);
      // Failure reports must not vanish after the default 8s — the user needs to read the names.
      toast.error(lines.join('\n'), 30_000);
    } else {
      toast.success(lines.join('\n'));
    }

    // Keep exactly the rows that failed selected, so a retry is one click.
    onRetain(result.failed.map((f) => f.item.id));
  }, [queryClient, t, onRetain]);

  const run = useCallback(async (
    items: Workflow[],
    op: (w: Workflow) => Promise<void>,
    successKey: string,
    invalidateFolders = false,
  ) => {
    if (items.length === 0) return;
    abortRef.current = false;
    setAbortRequested(false);
    const gen = captureAuthBoundaryGeneration();
    setProgress({ done: 0, total: items.length, current: '' });
    try {
      const result = await runBulkOperation(items, op, {
        getLabel: (w) => w.name,
        authBoundaryGeneration: gen,
        onProgress: setProgress,
        shouldAbort: () => abortRef.current,
      });
      report(result, items.length, successKey, invalidateFolders);
    } catch (err) {
      // An auth-boundary abort belongs to User A's session, which is gone — say nothing.
      if (!(err instanceof AuthBoundaryChangedError) && isAuthBoundaryGenerationCurrent(gen)) {
        toast.error(t('workflows:bulk.runFailed', { message: (err as Error).message }));
      }
    } finally {
      setProgress(null);
      setAbortRequested(false);
    }
  }, [report, t]);

  const deleteWorkflows = useCallback(async (items: Workflow[]) => {
    if (items.length === 0) return;
    const ok = await confirmDialog({
      message: t('workflows:bulk.deleteConfirm', { count: items.length }),
      danger: true,
    });
    if (!ok) return;
    await run(items, (w) => api.delete(`/workflows/${w.id}`), 'workflows:bulk.deleted');
  }, [run, t]);

  const moveWorkflows = useCallback(async (items: Workflow[], targetFolderId: string) => {
    await run(
      items,
      async (w) => {
        // Skip a no-op move — saves a round-trip and keeps the audit log clean.
        if ((w.folderId ?? ROOT_FOLDER_ID) === targetFolderId) throw new BulkSkippedError();
        await sharedFoldersApi.moveWorkflowToFolder(w.id, targetFolderId);
      },
      'workflows:bulk.moved',
      true,
    );
  }, [run]);

  const setEnabled = useCallback(async (items: Workflow[], enable: boolean) => {
    const path = enable ? 'enable' : 'disable';
    await run(
      items,
      async (w) => {
        if (w.isEnabled === enable) throw new BulkSkippedError();
        await api.post(`/workflows/${w.id}/${path}`, {});
      },
      enable ? 'workflows:bulk.enabled' : 'workflows:bulk.disabled',
    );
  }, [run]);

  const exportWorkflows = useCallback(async (items: Workflow[]) => {
    if (items.length === 0) return;
    abortRef.current = false;
    setAbortRequested(false);
    const gen = captureAuthBoundaryGeneration();
    const collected: unknown[] = [];
    setProgress({ done: 0, total: items.length, current: '' });
    try {
      const result = await runBulkOperation(
        items,
        async (w) => {
          const env = await api.get<WorkflowExportEnvelope>(`/workflows/${w.id}/export`);
          // A single-workflow export carries its item under `workflow`; re-pack everything into
          // the `workflows` array so the file is exactly what POST /workflows/import accepts.
          if (env.workflow) collected.push(env.workflow);
          else if (env.workflows) collected.push(...env.workflows);
        },
        {
          getLabel: (w) => w.name,
          authBoundaryGeneration: gen,
          onProgress: setProgress,
          shouldAbort: () => abortRef.current,
        },
      );

      if (collected.length > 0) {
        const envelope: WorkflowExportEnvelope = {
          schema: 'nodepilot-workflow-export/v1',
          exportVersion: 1,
          exportedAt: new Date().toISOString(),
          workflows: collected,
        };
        const stamp = new Date().toISOString().slice(0, 19).replaceAll(/[-:T]/g, '');
        downloadTextFile(
          `nodepilot-workflows-${stamp}.json`,
          JSON.stringify(envelope, null, 2),
          'application/json',
        );
      }
      // Export mutates nothing — reporting only, no cache invalidation needed, but the shared
      // reporter keeps the failure formatting identical to the other three actions.
      report(result, items.length, 'workflows:bulk.exported', false);
    } catch (err) {
      if (!(err instanceof AuthBoundaryChangedError) && isAuthBoundaryGenerationCurrent(gen)) {
        toast.error(t('common:exportFailed', { message: (err as Error).message }));
      }
    } finally {
      setProgress(null);
      setAbortRequested(false);
    }
  }, [report, t]);

  const requestAbort = useCallback(() => {
    abortRef.current = true;
    setAbortRequested(true);
  }, []);

  return {
    progress,
    running: progress !== null,
    abortRequested,
    requestAbort,
    deleteWorkflows,
    moveWorkflows,
    setEnabled,
    exportWorkflows,
  };
}
