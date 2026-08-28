import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { alertingApi } from '../api/alerting';
import { useAuthStore } from '../stores/authStore';

/** The subset of `/stats/dashboard` the sidebar badges read: three window-independent totals. */
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
 * Shares the dashboard's query key `['dashboard-stats', 24]`; the totals are window-independent,
 * so an open dashboard costs no extra request and its SignalR invalidation keeps the running
 * count current. The refetch interval keeps the counts fresh on other pages.
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
