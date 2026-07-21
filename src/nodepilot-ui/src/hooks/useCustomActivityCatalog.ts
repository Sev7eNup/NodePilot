import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { useCustomActivityCatalogStore, type CustomActivityCatalogEntry } from '../lib/customActivities';

/**
 * Fetches the runtime custom-activity catalog and mirrors it into the module-level cache + Zustand
 * store so both the reactive palette and the synchronous helpers (getActivityLabel /
 * describeNodeOutputs) see it. Call once where the designer mounts. SignalR/manual invalidation of
 * the ['custom-activities','catalog'] query key after CRUD keeps it fresh.
 */
export function useCustomActivityCatalog(includeDisabled = false) {
  const setCatalog = useCustomActivityCatalogStore((s) => s.setCatalog);
  const query = useQuery({
    queryKey: ['custom-activities', 'catalog', includeDisabled],
    queryFn: () =>
      api.get<CustomActivityCatalogEntry[]>(`/custom-activities${includeDisabled ? '?includeDisabled=true' : ''}`),
    staleTime: 60_000,
  });
  useEffect(() => {
    if (query.data) setCatalog(query.data);
  }, [query.data, setCatalog]);
  return query;
}
