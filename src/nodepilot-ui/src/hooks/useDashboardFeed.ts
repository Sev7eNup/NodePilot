import { useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';

function readCsrfToken(): string {
  if (typeof document === 'undefined') return '';
  const match = /(?:^|;\s*)np_csrf=([^;]+)/.exec(document.cookie);
  return match ? decodeURIComponent(match[1]) : '';
}

// A LiveEventsBatch item is { Type|type, Event|evt }. We only care about
// ExecutionStatusChanged — it is the only event that changes dashboard aggregates
// (running/recent/queue-depth/counts). StepStarted/StepCompleted don't move KPIs.
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
 * invalidates the dashboard-stats query so running/recent/queue KPIs reconcile in ~real
 * time instead of waiting for the 120 s polling fallback. Mirrors useOperationsFeed but
 * targets ['dashboard-stats'] (the dashboard's own cache key) rather than the operations
 * graph. SignalR failures are swallowed — the page still works off the polled snapshot.
 */
export function useDashboardFeed() {
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
        queryClient.invalidateQueries({ queryKey: ['dashboard-stats'] });
      }, 500);
    };

    connection.on('LiveEventsBatch', (batch: unknown) => {
      let sawStatus = false;
      for (const item of pickItems(batch)) {
        const s = asStatus(item);
        if (!s) continue;
        sawStatus = true;
      }
      // Any status transition (start/terminal) can move running/recent/queue/counts —
      // one debounced refetch covers a burst of events instead of N refetches.
      if (sawStatus) scheduleInvalidate();
    });

    const join = () => { connection.invoke('JoinOperationsFeed').catch(() => { /* RBAC reject / transient */ }); };
    connection.onreconnected(() => { if (!disposed) join(); });
    connection.start()
      .then(() => { if (!disposed) join(); })
      .catch(() => { /* offline / hub unavailable — snapshot polling still works */ });

    return () => {
      disposed = true;
      if (invalidateTimer.current !== null) clearTimeout(invalidateTimer.current);
      connection.invoke('LeaveOperationsFeed').catch(() => { /* ignore */ });
      void connection.stop();
    };
  }, [queryClient]);

  return null;
}