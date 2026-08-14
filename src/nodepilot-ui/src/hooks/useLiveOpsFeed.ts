import { useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { readCsrfToken } from '../api/csrf';
import { connectPersistently } from '../lib/signalrConnect';

// A LiveEventsBatch item is { Type|type, Event|evt }. We only care about ExecutionStatusChanged.
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
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub and debounce-
 * invalidates `queryKey` whenever a batch carried at least one ExecutionStatusChanged —
 * one refetch per burst instead of N. SignalR failures are swallowed; the consuming page
 * still works off its polled snapshot.
 *
 * `queryKey` and `onStatus` go into the effect deps, so both must be referentially stable
 * (a module-level constant / a zustand action) — an inline array would tear the connection
 * down and rebuild it on every render.
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
    // connectPersistently retries forever with capped backoff: the bare onreconnected +
    // one-shot start() gave up for good after ~40 s of outage (and never retried a failed
    // FIRST start at all), silently degrading this feed to snapshot polling until a reload.
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
