import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { alertingApi } from '../api/alerting';
import { useAuthStore } from '../stores/authStore';

/** The three window-independent totals the sidebar badges read. */
interface SidebarCounts {
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
 * Reads a dedicated three-count endpoint rather than the dashboard payload. The sidebar renders
 * on every page, so sharing the dashboard's key meant polling roughly twenty sequential queries
 * — including an unfiltered count over the whole executions table — everywhere in the app for
 * three numbers.
 *
 * The alerting-rule count is gated to Admin/Operator: `GET /api/alerting/rules` is not
 * Viewer-readable, so for a Viewer the query stays disabled and no alerts badge renders.
 */
export function useSidebarBadges(): SidebarBadges {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated) === true;
  const role = useAuthStore((s) => s.role);
  const canReadAlerts = role === 'Admin' || role === 'Operator';

  const stats = useQuery({
    queryKey: ['sidebar-counts'],
    queryFn: () => api.get<SidebarCounts>('/stats/sidebar-counts'),
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
