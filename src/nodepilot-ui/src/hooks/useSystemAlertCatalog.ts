import { useQuery } from '@tanstack/react-query';
import { systemAlertingApi } from '../api/systemAlerting';

/**
 * Hydrates the server-owned system-alert source catalog (part of the system alert policies
 * feature, see ADR 0008 for the design background). The catalog is the single source of
 * truth for source fields/units/operators/parameters/presets/availability — the UI renders from this rather
 * than a hand-maintained TypeScript mirror. Refreshed on a 60s staleness like the custom-activity catalog.
 */
export function useSystemAlertCatalog() {
  return useQuery({
    queryKey: ['system-alert-catalog'],
    queryFn: () => systemAlertingApi.catalog(),
    staleTime: 60_000,
  });
}
