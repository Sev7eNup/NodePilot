import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { DataBase } from '@carbon/icons-react';
import { useDbHealthStore } from '../../stores/dbHealthStore';

const PERSISTENT_OUTAGE_AFTER_MS = 60_000;

/**
 * The global "database is gone" banner.
 *
 * Rendered from App.tsx as a sibling of the router — NOT inside the layout shell. The designer
 * route (`/workflows/:id`) renders a bare Outlet without the shell, and losing work in the designer
 * is precisely the screen where this banner matters most. Anchored top-center because the
 * designer's bottom-left is occupied (xyflow Controls + a Panel).
 *
 * Copy escalates instead of staying cheerful: after 60 seconds the friendly "reconnecting" line
 * gains a "check the database service" instruction, and a server-rejected outage (wrong password,
 * missing database) says outright that an administrator has to act — a permanently optimistic
 * banner over a configuration error would be worse than the bug this feature fixes.
 */
export function DatabaseOutageBanner() {
  const { t } = useTranslation(['common']);
  const status = useDbHealthStore((s) => s.status);
  const reason = useDbHealthStore((s) => s.reason);
  const sinceUtc = useDbHealthStore((s) => s.sinceUtc);
  const rejected = reason === 'RejectedByServer';

  const [persistent, setPersistent] = useState(false);
  useEffect(() => {
    setPersistent(false);
    if (status !== 'unavailable' || rejected || !sinceUtc) return;

    const outageStartedAt = new Date(sinceUtc).getTime();
    if (!Number.isFinite(outageStartedAt)) return;

    const remainingMs = PERSISTENT_OUTAGE_AFTER_MS - (Date.now() - outageStartedAt);
    if (remainingMs <= 0) {
      setPersistent(true);
      return;
    }

    const timer = setTimeout(() => setPersistent(true), remainingMs);
    return () => clearTimeout(timer);
  }, [rejected, sinceUtc, status]);

  if (status !== 'unavailable') return null;

  const detail = rejected
    ? t('common:databaseOutage.rejected')
    : persistent
      ? t('common:databaseOutage.persistent')
      : t('common:databaseOutage.reconnecting');

  return (
    <div
      role="alert"
      aria-live="assertive"
      className="fixed top-16 left-1/2 -translate-x-1/2 z-[75] max-w-xl w-[calc(100%-2rem)]"
    >
      <div className="np-card border border-error/40 shadow-lg p-3 flex items-start gap-3">
        <DataBase size={20} className="text-error shrink-0 mt-0.5" aria-hidden="true" />
        <div className="min-w-0">
          <p className="font-semibold text-error text-sm">{t('common:databaseOutage.title')}</p>
          <p className="text-on-surface-variant text-sm mt-0.5">{detail}</p>
        </div>
        {!rejected && (
          <span
            className="ml-auto mt-1 size-2.5 shrink-0 rounded-full bg-error animate-pulse"
            title={t('common:databaseOutage.reconnecting')}
            aria-hidden="true"
          />
        )}
      </div>
    </div>
  );
}
