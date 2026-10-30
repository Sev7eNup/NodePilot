import { useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { createExecutionHubConnection } from '../lib/hubConnection';
import { connectPersistently } from '../lib/signalrConnect';
import { normalizeBatchItem } from './signalrReducer';
import type { LiveEventsBatch, LiveEventBatchItem } from './signalrTypes';
import {
  captureAuthBoundaryGeneration, isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';

// Payload casing compatibility is confined to this boundary; envelope decoding is shared.
interface StatusEvt {
  executionId?: string; ExecutionId?: string;
  workflowId?: string; WorkflowId?: string;
  status?: string; Status?: string;
}

function pickItems(batch: LiveEventsBatch): LiveEventBatchItem[] {
  if (Array.isArray(batch)) return batch;
  return batch?.events ?? batch?.Events ?? [];
}

function asStatus(item: LiveEventBatchItem): { executionId: string; workflowId: string; status: string } | null {
  const event = normalizeBatchItem(item);
  if (event?.type !== 'ExecutionStatusChanged') return null;
  const e: StatusEvt = event.evt;
  const executionId = e.executionId ?? e.ExecutionId;
  const workflowId = e.workflowId ?? e.WorkflowId;
  const status = e.status ?? e.Status;
  if (!executionId || !workflowId || !status) return null;
  return { executionId, workflowId, status };
}

/**
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub and
 * debounce-invalidates `queryKey` when a batch contains an ExecutionStatusChanged event, so
 * one burst costs one refetch. SignalR failures are ignored; the page still works off its
 * polled snapshot. `queryKey` and `onStatus` are effect dependencies, so both must be
 * referentially stable, otherwise the connection is rebuilt on every render.
 */
export function useLiveOpsFeed({
  queryKey, debounceMs, onStatus,
}: Readonly<{
  queryKey: unknown[];
  debounceMs: number;
  /** Optional per-event delta applied before the debounced snapshot reconciliation. */
  onStatus?: (executionId: string, workflowId: string, status: string) => void;
}>) {
  const queryClient = useQueryClient();
  const invalidateTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    let disposed = false;
    let hasConnected = false;
    const generation = captureAuthBoundaryGeneration();
    const current = () => !disposed && isAuthBoundaryGenerationCurrent(generation);
    const connection = createExecutionHubConnection();

    const scheduleInvalidate = () => {
      if (invalidateTimer.current !== null) return;
      invalidateTimer.current = setTimeout(() => {
        invalidateTimer.current = null;
        // A running refetch finishes instead of restarting: on a busy instance a slow refetch
        // would otherwise be cancelled by every burst and never complete.
        if (current()) void queryClient.invalidateQueries({ queryKey }, { cancelRefetch: false });
      }, debounceMs);
    };

    connection.on('LiveEventsBatch', (batch: LiveEventsBatch) => {
      if (!current()) return;
      let sawStatus = false;
      for (const item of pickItems(batch)) {
        const s = asStatus(item);
        if (!s) continue;
        onStatus?.(s.executionId, s.workflowId, s.status);
        sawStatus = true;
      }
      if (sawStatus) scheduleInvalidate();
    });

    const join = () => {
      if (current()) connection.invoke('JoinOperationsFeed').catch(() => { /* RBAC reject / transient */ });
    };
    // connectPersistently retries forever with capped backoff, so neither a long outage nor a
    // failed first start leaves the feed permanently degraded to snapshot polling.
    const persistent = connectPersistently(connection, () => {
      if (!current()) return;
      join();
      // History pages intentionally have no polling fallback. Reconcile changes missed
      // during the disconnect even when no subsequent batch arrives.
      if (hasConnected) scheduleInvalidate();
      hasConnected = true;
    });

    return () => {
      disposed = true;
      persistent.dispose();
      if (invalidateTimer.current !== null) clearTimeout(invalidateTimer.current);
      invalidateTimer.current = null;
      connection.invoke('LeaveOperationsFeed').catch(() => { /* ignore */ });
      void connection.stop();
    };
  }, [queryClient, queryKey, debounceMs, onStatus]);
}
