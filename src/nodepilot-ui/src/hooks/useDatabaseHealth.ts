import { useEffect, useRef } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useDbHealthStore } from '../stores/dbHealthStore';
import { toast } from '../stores/toastStore';

/**
 * The SPA's database-health probe. Mounted once, in App; the banner, the TopBar pill and the
 * auth re-probe all read the store this hook feeds.
 *
 * Polls the anonymous, memory-only `/healthz/database`, which answers 200 in every state: a 503
 * there would be indistinguishable from an unreachable process. It polls every 15 s while healthy
 * and every 3 s while an outage is suspected (a request saw a DATABASE_* 503) or confirmed, so
 * recovery is noticed quickly without a chatty poll during normal operation.
 */
export function useDatabaseHealth(): void {
  const { t } = useTranslation(['common']);
  const queryClient = useQueryClient();
  const status = useDbHealthStore((s) => s.status);
  const suspectedAt = useDbHealthStore((s) => s.suspectedAt);

  const fast = suspectedAt !== null || status === 'unavailable' || status === 'armed' || status === 'offline';

  useQuery({
    queryKey: ['database-health'],
    queryFn: async () => {
      const store = useDbHealthStore.getState();
      try {
        const res = await fetch('/healthz/database', {
          cache: 'no-store',
          signal: AbortSignal.timeout(5000),
        });

        // Check the content type before res.json(): the SPA fallback route serves index.html
        // with status 200 for any unmatched extensionless path, so on a build without this
        // endpoint parsing that HTML would throw on every poll and the pill would show a
        // permanent outage with no diagnosis.
        const contentType = res.headers.get('content-type') ?? '';
        if (!res.ok || !contentType.includes('application/json')) {
          store.reportProbeFailed();
          return null;
        }

        const dto = (await res.json()) as { status?: string; sinceUtc?: string | null; reason?: string | null };
        store.reportProbeResult({
          status: dto.status ?? 'unknown',
          sinceUtc: dto.sinceUtc ?? null,
          reason: dto.reason ?? null,
        });
        return null;
      } catch {
        // A network error or timeout means the process is unreachable, which is a different
        // message than a missing database. Never rethrow: this query must not feed the global
        // error toast.
        store.reportProbeFailed();
        return null;
      }
    },
    refetchInterval: fast ? 3_000 : 15_000,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    retry: false,
    staleTime: 0,
    meta: { silentError: true },
  });

  // Recovery handling lives here rather than in the store so it happens exactly once per
  // transition, with access to the query client.
  const previousStatus = useRef(status);
  useEffect(() => {
    const wasDown = previousStatus.current === 'unavailable' || previousStatus.current === 'offline';
    previousStatus.current = status;
    if (!wasDown || status !== 'ok') return;

    // Every query on screen was either 503'd or served from a stale cache during the outage.
    // One global invalidation refreshes them all now that the database answers again.
    void queryClient.invalidateQueries();
    toast.success(t('common:databaseOutage.recovered'));
  }, [status, queryClient, t]);
}
