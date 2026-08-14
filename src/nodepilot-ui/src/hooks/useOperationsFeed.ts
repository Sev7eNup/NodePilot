import { useOperationsStore } from '../stores/operationsStore';
import { useLiveOpsFeed } from './useLiveOpsFeed';

// Module-level so the identity is stable across renders — it feeds useLiveOpsFeed's effect deps.
const OPERATIONS_GRAPH_KEY = ['operations-graph'];

/**
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub. Applies
 * ExecutionStatusChanged deltas to the operations store (instant timeline/ticker updates)
 * and debounce-invalidates the snapshot query so the timeline reconciles against the
 * authoritative snapshot. SignalR failures are swallowed (the page still works off the
 * polled snapshot).
 */
export function useOperationsFeed() {
  const applyStatus = useOperationsStore((s) => s.applyStatus);
  useLiveOpsFeed({ queryKey: OPERATIONS_GRAPH_KEY, debounceMs: 800, onStatus: applyStatus });
}
