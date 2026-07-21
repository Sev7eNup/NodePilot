import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import type { MetricsDashboard, ObservabilityConfig, TelemetrySummary } from '../types/api';

export function useObservabilityConfig() {
  return useQuery({
    queryKey: ['observability-config'],
    queryFn: () => api.get<ObservabilityConfig>('/observability/config'),
    staleTime: 10 * 60 * 1000,
    gcTime: 60 * 60 * 1000,
    retry: false,
  });
}

export function useTelemetrySummary(enabled: boolean) {
  return useQuery({
    queryKey: ['observability-summary'],
    queryFn: () => api.get<TelemetrySummary>('/observability/summary'),
    refetchInterval: 15_000,
    enabled,
  });
}

export function useMetricsDashboard(key: string, hours: number, paused: boolean, enabled: boolean) {
  return useQuery({
    queryKey: ['metrics-dashboard', key, hours],
    queryFn: () => api.get<MetricsDashboard>(`/observability/dashboards/${encodeURIComponent(key)}?hours=${hours}`),
    enabled,
    refetchInterval: paused ? false : 30_000,
  });
}

export function buildGrafanaDashboardUrl(baseUrl: string | null | undefined, uid: string): string | null {
  if (!baseUrl || !uid) return null;
  try {
    const url = new URL(baseUrl);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    url.pathname = `${url.pathname.replace(/\/$/, '')}/d/${encodeURIComponent(uid)}`;
    return url.toString();
  } catch { return null; }
}

export function buildTraceUrl(template: string | null | undefined, traceId: string | null | undefined): string | null {
  if (!template || !traceId) return null;
  const raw = template.replace('{traceId}', encodeURIComponent(traceId));
  // Whitelist http(s) only — a maliciously configured template like `javascript:fetch(...)`
  // would otherwise end up verbatim in an <a href> and execute on click, exfiltrating the JWT.
  try {
    const url = new URL(raw);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    return url.toString();
  } catch {
    return null;
  }
}
