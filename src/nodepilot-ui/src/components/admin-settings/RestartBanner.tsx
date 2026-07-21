import { WarningFilled } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { adminSettings } from '../../api/adminSettings';

/**
 * Polls <c>/api/admin/settings/status</c> every 30s and renders an orange banner when
 * the backend reports a pending restart. The banner names every section whose save is
 * waiting on a service restart, so the operator can see at a glance whether a single
 * "save SMTP, restart, move on" is enough or whether multiple unsaved-pending sections
 * are piling up across browser sessions.
 *
 * <para>Polling cadence (30s) is a deliberate compromise: tight enough that a fresh
 * save lights up the banner within a window of the next tab focus, loose enough that
 * a dozen open Settings tabs don't flood the API.</para>
 */
export function RestartBanner() {
  const { t, i18n } = useTranslation(['adminSettings']);

  const { data } = useQuery({
    queryKey: ['admin-settings', 'status'],
    queryFn: () => adminSettings.getStatus(),
    refetchInterval: 30_000,
    // Refetch on window focus is otherwise off globally — re-enable for the status
    // probe so an operator who comes back to the tab after a restart sees the banner
    // disappear immediately instead of waiting for the next 30s tick.
    refetchOnWindowFocus: true,
  });

  if (!data?.restartRequired) return null;

  const sinceLabel = data.restartRequiredSince
    ? new Date(data.restartRequiredSince).toLocaleString(i18n.language)
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
      </div>
    </div>
  );
}
