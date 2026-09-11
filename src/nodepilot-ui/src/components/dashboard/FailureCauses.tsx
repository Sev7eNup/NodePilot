import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { formatDate, formatNumber, formatRelative } from '../../lib/format';

interface FailureCause {
  message: string | null;
  count: number;
  latestExecutionId: string;
  latestStartedAt: string;
}

interface FailureCausesResponse {
  totalFailed: number;
  groups: FailureCause[];
  remainingCount: number;
}

export function FailureCauses({ windowHours }: Readonly<{ windowHours: number }>) {
  const { t } = useTranslation(['dashboard', 'common']);
  const { data, isPending, isError, refetch } = useQuery({
    // Separate from dashboard-stats: the live feed must not rerun this aggregation on every event.
    queryKey: ['dashboard-failure-causes', windowHours],
    queryFn: () => api.get<FailureCausesResponse>(`/stats/failure-causes?windowHours=${windowHours}`),
    refetchInterval: 120_000,
    staleTime: 120_000,
    retry: false,
  });

  return (
    <div className="relative flex-1 min-h-[240px]" aria-busy={isPending}>
      <div className="absolute inset-0 overflow-y-auto overscroll-contain pr-1">
        {isPending ? (
          <p role="status" className="text-outline text-sm py-4 text-center">{t('common:loadingDots')}</p>
        ) : isError ? (
          <div role="alert" className="flex flex-col items-center gap-3 py-4 text-sm text-center">
            <p className="text-on-surface-variant">{t('dashboard:failureCauses.error')}</p>
            <button type="button" className="np-btn np-btn-secondary" onClick={() => void refetch()}>
              {t('dashboard:failureCauses.retry')}
            </button>
          </div>
        ) : data && data.totalFailed > 0 ? (
          <>
            <p className="text-[11px] text-outline mb-2">
              {t('dashboard:failureCauses.total', { count: data.totalFailed, formattedCount: formatNumber(data.totalFailed) })}
            </p>
            <ul key={windowHours} className="divide-y divide-outline-variant/30">
              {data.groups.map((group) => (
                <FailureCauseRow key={group.message ?? ''} group={group} total={data.totalFailed} />
              ))}
            </ul>
            {data.remainingCount > 0 && (
              <p className="text-[11px] text-outline pt-3">
                {t('dashboard:failureCauses.remaining', { count: data.remainingCount, formattedCount: formatNumber(data.remainingCount) })}
              </p>
            )}
          </>
        ) : (
          <p className="text-outline text-sm py-4 text-center">{t('dashboard:failureCauses.empty')}</p>
        )}
      </div>
    </div>
  );
}

function FailureCauseRow({ group, total }: Readonly<{ group: FailureCause; total: number }>) {
  const { t, i18n } = useTranslation(['dashboard']);
  const [expanded, setExpanded] = useState(false);
  const message = group.message ?? t('dashboard:failureCauses.noMessage');
  const long = message.length > 220;
  const percent = new Intl.NumberFormat(i18n.language, { style: 'percent', maximumFractionDigits: 1 }).format(group.count / total);

  return (
    <li className="py-2.5 first:pt-1">
      <Link
        to={`/executions?id=${encodeURIComponent(group.latestExecutionId)}`}
        className="block rounded text-xs text-on-surface hover:text-primary focus-visible:outline-2 focus-visible:outline-primary focus-visible:outline-offset-2"
        aria-label={t('dashboard:failureCauses.openLatest', { message })}
      >
        <span className="block font-medium whitespace-pre-wrap [overflow-wrap:anywhere]">
          {long && !expanded ? `${message.slice(0, 220)}…` : message}
        </span>
        <span className="mt-1.5 flex flex-wrap items-center justify-between gap-x-3 gap-y-1 text-[11px]">
          <span className="font-semibold tabular-nums text-red-600 dark:text-red-400">
            {t('dashboard:failureCauses.occurrences', { count: group.count, formattedCount: formatNumber(group.count) })} · {percent}
          </span>
          <span className="text-outline" title={formatDate(group.latestStartedAt)}>
            {t('dashboard:failureCauses.latest', { time: formatRelative(group.latestStartedAt) })}
          </span>
        </span>
      </Link>
      {long && (
        <button
          type="button"
          aria-expanded={expanded}
          className="mt-1.5 text-[11px] text-primary hover:underline rounded focus-visible:outline-2 focus-visible:outline-primary"
          onClick={() => setExpanded(!expanded)}
        >
          {t(expanded ? 'dashboard:failureCauses.collapse' : 'dashboard:failureCauses.expand')}
        </button>
      )}
    </li>
  );
}
