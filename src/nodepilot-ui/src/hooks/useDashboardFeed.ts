import { useLiveOpsFeed } from './useLiveOpsFeed';

// Module-level so the identity is stable across renders — it feeds useLiveOpsFeed's effect deps.
const DASHBOARD_STATS_KEY = ['dashboard-stats'];

/**
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub and debounce-
 * invalidates the dashboard-stats query so running/recent/queue KPIs reconcile in ~real
 * time instead of waiting for the 120 s polling fallback. Mirrors useOperationsFeed but
 * targets ['dashboard-stats'] (the dashboard's own cache key) rather than the operations
 * graph. Any status transition (start/terminal) can move running/recent/queue/counts, so
 * no per-event delta is applied — one debounced refetch covers the burst.
 */
export function useDashboardFeed() {
  useLiveOpsFeed({ queryKey: DASHBOARD_STATS_KEY, debounceMs: 500 });
  return null;
}
