import { useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { readCsrfToken } from '../api/csrf';
import { connectPersistently } from '../lib/signalrConnect';

// A LiveEventsBatch item is { Type|type, Event|evt }. Only ExecutionStatusChanged is handled.
interface StatusEvt {
  executionId?: string; ExecutionId?: string;
  workflowId?: string; WorkflowId?: string;
  status?: string; Status?: string;
}

function pickItems(batch: unknown): unknown[] {
  if (Array.isArray(batch)) return batch;
  const b = batch as { events?: unknown[]; Events?: unknown[] } | null;
  return b?.events ?? b?.Events ?? [];
}

function asStatus(item: unknown): { executionId: string; workflowId: string; status: string } | null {
  const it = item as { Type?: string; type?: string; Event?: StatusEvt; evt?: StatusEvt };
  const type = it.Type ?? it.type;
  if (type !== 'ExecutionStatusChanged') return null;
  const e = it.Event ?? it.evt;
  if (!e) return null;
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
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/execution', { headers: { 'X-CSRF-Token': readCsrfToken() } })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    const scheduleInvalidate = () => {
      if (invalidateTimer.current !== null) return;
      invalidateTimer.current = setTimeout(() => {
        invalidateTimer.current = null;
        queryClient.invalidateQueries({ queryKey });
      }, debounceMs);
    };

    connection.on('LiveEventsBatch', (batch: unknown) => {
      let sawStatus = false;
      for (const item of pickItems(batch)) {
        const s = asStatus(item);
        if (!s) continue;
        onStatus?.(s.executionId, s.workflowId, s.status);
        sawStatus = true;
      }
      if (sawStatus) scheduleInvalidate();
    });

    const join = () => { connection.invoke('JoinOperationsFeed').catch(() => { /* RBAC reject / transient */ }); };
    // connectPersistently retries forever with capped backoff, so neither a long outage nor a
    // failed first start leaves the feed permanently degraded to snapshot polling.
    const persistent = connectPersistently(connection, () => { if (!disposed) join(); });

    return () => {
      disposed = true;
      persistent.dispose();
      if (invalidateTimer.current !== null) clearTimeout(invalidateTimer.current);
      connection.invoke('LeaveOperationsFeed').catch(() => { /* ignore */ });
      void connection.stop();
    };
  }, [queryClient, queryKey, debounceMs, onStatus]);
}
