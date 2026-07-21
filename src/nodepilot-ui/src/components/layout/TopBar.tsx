import { BareMetalServer, ChevronRight, Menu, Plug } from '@carbon/icons-react';
import { useMemo } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { systemApi } from '../../api/system';
import { useAuthStore } from '../../stores/authStore';
import { resolveBreadcrumbs } from '../../lib/breadcrumbs';

/**
 * App header strip (right of the sidebar). Shows a route-aware breadcrumb on the left
 * and a live backend-connectivity indicator on the right. The indicator polls the
 * backend's anonymous liveness endpoint (`/healthz/live`) on an interval; any non-2xx
 * response or network error flips it to "unreachable".
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
 * Inline host identity (machine name, FQDN, DNS domain) shown in the header so any signed-in
 * user can see at a glance which server answered — useful in active/passive HA where several
 * nodes may serve the SPA. All fields are visible at once, separated by thin dividers; hidden
 * below `md` so they never crowd the title on narrow viewports.
 *
 * Degrades silently: while unauthenticated the query is disabled, and any non-object response
 * (e.g. the hermetic e2e catch-all answering `[]`) renders nothing rather than a broken row.
 */
function HostIdentityInfo() {
  const { t } = useTranslation(['common']);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  const { data } = useQuery({
    queryKey: ['host-info'],
    queryFn: systemApi.getHostInfo,
    // Host identity is fixed for a given backend — fetch once, never poll, don't retry.
    enabled: isAuthenticated === true,
    staleTime: Infinity,
    retry: false,
  });

  // Only render once we have a well-shaped object (guards against `[]` / undefined).
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

  const { data: online, isLoading, isError } = useQuery({
    queryKey: ['backend-health'],
    queryFn: async () => {
      // Raw fetch (not the /api client): the health endpoint lives at /healthz/live.
      // AbortSignal.timeout keeps a hung backend from leaving the pill stuck on "checking".
      const res = await fetch('/healthz/live', {
        cache: 'no-store',
        signal: AbortSignal.timeout(5000),
      });
      if (!res.ok) throw new Error(`status ${res.status}`);
      return true;
    },
    refetchInterval: 15_000,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    retry: false,
    staleTime: 10_000,
  });

  // First load (no prior data) reads as "checking"; after that, error → offline.
  const state: 'checking' | 'online' | 'offline' =
    isLoading && online === undefined ? 'checking' : (isError || !online) ? 'offline' : 'online';

  const meta = {
    checking: { icon: 'text-amber-500 animate-pulse', label: t('common:backend.checking') },
    online: { icon: 'text-green-500', label: t('common:backend.connected') },
    offline: { icon: 'text-red-500 dark:text-red-400', label: t('common:backend.unreachable') },
  }[state];

  return (
    <span
      aria-label={`API: ${meta.label}`}
      className="inline-flex items-center gap-1.5 text-xs font-label"
      title={t('common:backend.tooltip')}
    >
      <Plug size={14} strokeWidth={2.25} className={meta.icon} aria-hidden="true" />
      <span className="text-on-surface-variant font-semibold tracking-wide">API</span>
    </span>
  );
}
