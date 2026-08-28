import { useOperationsStore } from '../stores/operationsStore';
import { useLiveOpsFeed } from './useLiveOpsFeed';

// Module-level so the identity stays stable across renders; it feeds useLiveOpsFeed effect deps.
const OPERATIONS_GRAPH_KEY = ['operations-graph'];

/**
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub. Applies
 * ExecutionStatusChanged deltas to the operations store for immediate timeline and ticker
 * updates, and debounce-invalidates the snapshot query so the timeline reconciles against the
 * authoritative snapshot. SignalR failures are ignored; the page still works off polled data.
 */
export function useOperationsFeed() {
  const applyStatus = useOperationsStore((s) => s.applyStatus);
  useLiveOpsFeed({ queryKey: OPERATIONS_GRAPH_KEY, debounceMs: 800, onStatus: applyStatus });
}
