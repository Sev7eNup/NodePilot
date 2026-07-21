import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { alertingApi } from '../api/alerting';
import { useAuthStore } from '../stores/authStore';

/** The subset of `/stats/dashboard` the sidebar badges need. The endpoint returns far
 *  more; we only read these three window-independent totals. */
interface DashboardCounts {
  workflowsTotal: number;
  runningCount: number;
  machinesTotal: number;
}

export interface SidebarBadges {
  workflows?: number;
  running?: number;
  machines?: number;
  alerts?: number;
}

/**
 * Live counts for the sidebar nav badges.
 *
 * Reuses the dashboard's own query key `['dashboard-stats', 24]` (the DashboardPage default
 * window) — the totals it reads are window-independent, so sharing the key means zero extra
 * request while the dashboard is open, and the dashboard's SignalR invalidation refreshes the
 * running count there in ~real time. A gentle `refetchInterval` keeps counts current elsewhere.
 *
 * The alerting-rule count is gated to Admin/Operator: `GET /api/alerting/rules` is not
 * Viewer-readable, so for a Viewer the query stays disabled and no alerts badge renders.
 */
export function useSidebarBadges(): SidebarBadges {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated) === true;
  const role = useAuthStore((s) => s.role);
  const canReadAlerts = role === 'Admin' || role === 'Operator';

  const stats = useQuery({
    queryKey: ['dashboard-stats', 24],
    queryFn: () => api.get<DashboardCounts>('/stats/dashboard?windowHours=24'),
    enabled: isAuthenticated,
    staleTime: 30_000,
    refetchInterval: 60_000,
  });

  const rules = useQuery({
    queryKey: ['alerting-rules'],
    queryFn: alertingApi.list,
    enabled: isAuthenticated && canReadAlerts,
    staleTime: 60_000,
    refetchInterval: 120_000,
  });

  return {
    workflows: stats.data?.workflowsTotal,
    running: stats.data?.runningCount,
    machines: stats.data?.machinesTotal,
    alerts: rules.data?.length,
  };
}
