import { useLiveOpsFeed } from './useLiveOpsFeed';

// Module-level so the identity stays stable across renders; it feeds useLiveOpsFeed's effect deps.
const DASHBOARD_STATS_KEY = ['dashboard-stats'];

/**
 * Subscribes to the RBAC-scoped live-ops feed on the shared execution hub and debounce-
 * invalidates the dashboard-stats query, so the running, recent and queue KPIs update
 * without waiting for the polling fallback. Mirrors useOperationsFeed but targets
 * ['dashboard-stats'], the dashboard's own cache key, rather than the operations graph.
 * Any status transition can move several counters, so one debounced refetch replaces
 * per-event deltas.
 */
export function useDashboardFeed() {
  useLiveOpsFeed({ queryKey: DASHBOARD_STATS_KEY, debounceMs: 500 });
  return null;
}
