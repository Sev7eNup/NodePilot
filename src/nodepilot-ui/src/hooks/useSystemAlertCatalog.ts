import { useQuery } from '@tanstack/react-query';
import { systemAlertingApi } from '../api/systemAlerting';

/**
 * Loads the server-owned system-alert source catalog (ADR 0008). The catalog is the single
 * source of truth for source fields, units, operators, parameters, presets and availability,
 * so the UI renders from it instead of a hand-maintained TypeScript mirror.
 */
export function useSystemAlertCatalog() {
  return useQuery({
    queryKey: ['system-alert-catalog'],
    queryFn: () => systemAlertingApi.catalog(),
    staleTime: 60_000,
  });
}
