import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { alertingApi } from '../api/alerting';
import { countCredentialsNeedingAttention } from '../lib/credentialExpiry';
import { useAuthStore } from '../stores/authStore';
import type { Credential } from '../types/api';

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
  credentials?: number;
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
 * The alerting-rule and credential counts are gated to Admin/Operator: neither list is
 * Viewer-readable, so for a Viewer both queries stay disabled and neither badge renders. The
 * credential query shares its key with the pages that list credentials, so a save there
 * updates the badge at once.
 */
export function useSidebarBadges(): SidebarBadges {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated) === true;
  const role = useAuthStore((s) => s.role);
  const canWrite = role === 'Admin' || role === 'Operator';

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
    enabled: isAuthenticated && canWrite,
    staleTime: 60_000,
    refetchInterval: 120_000,
  });

  const credentials = useQuery({
    queryKey: ['credentials'],
    queryFn: () => api.get<Credential[]>('/credentials'),
    enabled: isAuthenticated && canWrite,
    staleTime: 60_000,
    refetchInterval: 300_000,
    select: (rows) => countCredentialsNeedingAttention(rows, Date.now()),
  });

  return {
    workflows: stats.data?.workflowsTotal,
    running: stats.data?.runningCount,
    machines: stats.data?.machinesTotal,
    credentials: credentials.data,
    alerts: rules.data?.length,
  };
}
