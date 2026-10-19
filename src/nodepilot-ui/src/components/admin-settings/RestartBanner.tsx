import { useState } from 'react';
import { WarningFilled } from '@carbon/icons-react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { adminSettings } from '../../api/adminSettings';
import { formatDate } from '../../lib/format';
import { confirmDialog } from '../../stores/confirmStore';
import { toast } from '../../stores/toastStore';

const RESTART_POLL_MS = 2_000;
// The SCM waits 5 s before restarting; start-up adds database readiness on top.
const RESTART_GIVE_UP_MS = 120_000;

/**
 * Polls /api/admin/settings/status every 30s and shows an orange banner when the backend
 * reports a pending restart, naming every section whose save is waiting on it. The 30s
 * interval balances freshness (the banner updates within one tab focus) against load from
 * many open Settings tabs.
 *
 * When the server can restart its own Windows service, the banner offers the restart. After
 * the request the status is polled faster: the banner disappears once the new process has
 * started, because only then is the restart marker cleared.
 */
export function RestartBanner() {
  const { t } = useTranslation(['adminSettings']);
  const [restartPhase, setRestartPhase] = useState<'idle' | 'waiting' | 'timedOut'>('idle');
  const restarting = restartPhase !== 'idle';

  const { data } = useQuery({
    queryKey: ['admin-settings', 'status'],
    queryFn: () => adminSettings.getStatus(),
    refetchInterval: restarting ? RESTART_POLL_MS : 30_000,
    // Refetch on window focus is otherwise off globally — re-enable for the status
    // probe so an operator who comes back to the tab after a restart sees the banner
    // disappear immediately instead of waiting for the next 30s tick.
    refetchOnWindowFocus: true,
  });

  const restart = useMutation({
    mutationFn: () => adminSettings.restartService(),
    onSuccess: () => {
      setRestartPhase('waiting');
      globalThis.setTimeout(
        () => setRestartPhase((phase) => (phase === 'waiting' ? 'timedOut' : phase)),
        RESTART_GIVE_UP_MS,
      );
    },
    onError: (err: Error) => toast.error(err.message),
  });

  if (!data?.restartRequired) return null;

  const timedOut = restartPhase === 'timedOut';

  async function onRestart() {
    if (await confirmDialog({
      title: t('adminSettings:restartServiceConfirmTitle'),
      message: t('adminSettings:restartServiceConfirmBody'),
      confirmLabel: t('adminSettings:restartServiceConfirmAction'),
      danger: true,
    })) restart.mutate();
  }

  const sinceLabel = data.restartRequiredSince
    ? formatDate(data.restartRequiredSince)
    : '';

  return (
    <div
      className="flex items-start gap-3 px-4 py-3 rounded-md bg-amber-50 dark:bg-amber-950/50 border border-amber-200 dark:border-amber-800/40 text-amber-900 dark:text-amber-300"
      role="alert"
    >
      <WarningFilled className="shrink-0 mt-0.5" size={18} />
      <div className="flex-1">
        <p className="font-semibold text-sm">{t('adminSettings:restartBannerTitle')}</p>
        <p className="text-sm mt-0.5">{t('adminSettings:restartBannerBody')}</p>
        <ul className="text-sm mt-1.5 flex flex-wrap gap-2">
          {data.restartRequiredFor.map((section) => (
            <li
              key={section}
              className="px-2 py-0.5 rounded bg-amber-100 dark:bg-amber-900/40 border border-amber-300 dark:border-amber-800/50 text-xs font-mono"
              aria-label={t('adminSettings:restartBannerSectionAria', { section })}
            >
              {section}
            </li>
          ))}
        </ul>
        {sinceLabel && (
          <p className="text-xs mt-1 text-amber-700 dark:text-amber-400">
            {t('adminSettings:restartBannerSince', { since: sinceLabel })}
          </p>
        )}
        {restarting && (
          <p className="text-sm mt-2 font-medium" role="status">
            {timedOut ? t('adminSettings:restartServiceTimedOut') : t('adminSettings:restartServiceInProgress')}
          </p>
        )}
      </div>
      {data.canRestartService && (
        <button
          type="button"
          onClick={() => void onRestart()}
          disabled={restart.isPending || (restarting && !timedOut)}
          className="shrink-0 rounded-lg px-3 py-1.5 text-sm font-medium bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {t('adminSettings:restartServiceButton')}
        </button>
      )}
    </div>
  );
}
