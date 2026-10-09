import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';

/** Quartz and the server's time zone own schedule semantics; the browser only formats dates. */
export function useSchedulePreview(cron: string, enabled = true) {
  const [settledCron, setSettledCron] = useState(cron);
  useEffect(() => {
    const timer = setTimeout(() => setSettledCron(cron), 250);
    return () => clearTimeout(timer);
  }, [cron]);
  const ready = enabled && !!cron.trim() && settledCron === cron;
  const query = useQuery({
    queryKey: ['schedule-next-fires', settledCron],
    queryFn: () => api.get<{ fires: string[] }>(
      `/triggers/schedule/next-fires?cron=${encodeURIComponent(settledCron)}&count=5`),
    enabled: ready,
    staleTime: 30_000,
    refetchInterval: ready ? 30_000 : false,
    retry: false,
    meta: { silentError: true },
  });
  const fireTimes = useMemo(() => ready && query.data
    ? query.data.fires.map(value => new Date(value)) : [], [ready, query.data]);
  return {
    fireTimes,
    isLoading: enabled && !!cron.trim() && (!ready || query.isPending),
    error: ready && query.error ? query.error.message : null,
  };
}
