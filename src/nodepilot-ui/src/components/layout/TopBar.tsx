import { BareMetalServer, ChevronRight, Menu, Plug } from '@carbon/icons-react';
import { useMemo } from 'react';
import { Link, useLocation } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { systemApi } from '../../api/system';
import { useAuthStore } from '../../stores/authStore';
import { useDbHealthStore } from '../../stores/dbHealthStore';
import { resolveBreadcrumbs } from '../../lib/breadcrumbs';

/**
 * App header strip to the right of the sidebar. Shows a route-aware breadcrumb on the left,
 * and the host identity plus a backend connectivity indicator on the right.
 */
export function TopBar({ onOpenMenu }: Readonly<{ onOpenMenu?: () => void }> = {}) {
  const { t } = useTranslation(['nav', 'common', 'adminSettings', 'alerts', 'backup', 'metrics']);
  const { pathname, search } = useLocation();
  const role = useAuthStore((state) => state.role);
  const breadcrumbs = useMemo(
    () => resolveBreadcrumbs(pathname, search, role),
    [pathname, search, role],
  );

  return (
    <header className="shrink-0 h-12 px-3 sm:px-4 lg:px-6 flex items-center justify-between gap-2 border-b border-outline-variant/40 bg-surface-low/60 backdrop-blur-sm">
      <div className="flex flex-1 items-center gap-1 min-w-0 overflow-hidden">
        <button
          onClick={onOpenMenu}
          title={t('nav:openMenu')}
          aria-label={t('nav:openMenu')}
          className="lg:hidden -ml-1 shrink-0 p-1.5 rounded text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
        >
          <Menu size={18} />
        </button>
        {breadcrumbs.length > 0 && (
          <nav aria-label={t('nav:breadcrumb')} className="min-w-0 overflow-hidden">
            <ol className="flex min-w-0 items-center gap-1 text-sm whitespace-nowrap">
              {breadcrumbs.map((crumb, index) => {
                const current = index === breadcrumbs.length - 1;
                return (
                  <li
                    key={`${crumb.labelKey}-${index}`}
                    className={`flex min-w-0 items-center gap-1 ${current ? 'flex-1' : 'shrink'}`}
                  >
                    {index > 0 && (
                      <ChevronRight size={13} aria-hidden="true" className="shrink-0 text-outline/70" />
                    )}
                    {current ? (
                      <h1
                        aria-current="page"
                        className="truncate font-headline font-semibold text-on-surface"
                        title={t(crumb.labelKey)}
                      >
                        {t(crumb.labelKey)}
                      </h1>
                    ) : (
                      <Link
                        to={crumb.to ?? '#'}
                        className="block max-w-28 truncate rounded-sm font-label text-on-surface-variant transition-colors hover:text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/50 sm:max-w-36"
                      >
                        {t(crumb.labelKey)}
                      </Link>
                    )}
                  </li>
                );
              })}
            </ol>
          </nav>
        )}
      </div>
      <div className="flex items-center gap-3 shrink-0">
        <HostIdentityInfo />
        <BackendStatus />
      </div>
    </header>
  );
}

/**
 * Inline host identity shown in the header so any signed-in user can tell which server answered,
 * which matters in active/passive HA where several nodes serve the SPA. Hidden below `md` so it
 * does not crowd the title on narrow viewports.
 *
 * Renders nothing while unauthenticated (the query is disabled) or when the response is not a
 * well-shaped object, rather than showing a broken row.
 */
function HostIdentityInfo() {
  const { t } = useTranslation(['common']);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  const { data } = useQuery({
    queryKey: ['host-info'],
    queryFn: systemApi.getHostInfo,
    // Host identity is fixed for a given backend: fetch once, never poll, never retry.
    enabled: isAuthenticated === true,
    staleTime: Infinity,
    retry: false,
  });

  // Only render once the response is a well-shaped object (guards against `[]` and undefined).
  if (!data || typeof data.machineName !== 'string') return null;

  const fqdn = typeof data.fqdn === 'string' ? data.fqdn.trim() : '';
  const hostName = fqdn.includes('.')
    ? fqdn
    : data.machineName;

  return (
    <div
      className="hidden md:flex items-center gap-2.5 text-xs whitespace-nowrap"
      title={t('common:host.tooltip')}
    >
      <BareMetalServer size={13} className="shrink-0 text-outline" />
      <Field label={t('common:host.machine')} value={hostName} />
    </div>
  );
}

function Field({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <span className="inline-flex items-baseline gap-1">
      <span className="text-[10px] font-label uppercase tracking-wide text-outline">{label}</span>
      <span className="font-medium text-on-surface-variant">{value}</span>
    </span>
  );
}

function BackendStatus() {
  const { t } = useTranslation(['common']);

  // Fed by the single useDatabaseHealth poll mounted in App; this component runs no query itself.
  // That poll uses /healthz/database rather than /healthz/live, which by design stays 200 through
  // a database outage. A network failure against it still means the process is down (offline),
  // and it additionally distinguishes an unreachable database.
  const status = useDbHealthStore((s) => s.status);

  const state: 'checking' | 'online' | 'dbDown' | 'offline' =
    status === 'unknown' ? 'checking'
      : status === 'offline' ? 'offline'
        : status === 'unavailable' ? 'dbDown'
          : 'online';

  // 'armed' renders as online: it only means one query was slow, which the operator cannot act
  // on, and a flickering pill would train people to ignore the indicator.
  const meta = {
    checking: { icon: 'text-amber-500 animate-pulse', label: t('common:backend.checking') },
    online: { icon: 'text-green-500', label: t('common:backend.connected') },
    // Orange rather than amber, because amber with a pulse is the checking state, and separate
    // from red because here the process answers while its database does not.
    dbDown: { icon: 'text-orange-600 dark:text-orange-400', label: t('common:backend.databaseUnreachable') },
    offline: { icon: 'text-red-500 dark:text-red-400', label: t('common:backend.unreachable') },
  }[state];

  return (
    <span
      aria-label={`API: ${meta.label}`}
      className="inline-flex items-center gap-1.5 text-xs font-label"
      title={meta.label}
    >
      <Plug size={14} strokeWidth={2.25} className={meta.icon} aria-hidden="true" />
      <span className="text-on-surface-variant font-semibold tracking-wide">API</span>
    </span>
  );
}
