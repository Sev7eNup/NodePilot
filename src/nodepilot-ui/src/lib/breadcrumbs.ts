import { metricsSections, navGroups } from './navigation';

export type BreadcrumbItem = { labelKey: string; to?: string };

const settingsSectionKeys: Record<string, string> = {
  integrations: 'adminSettings:subTabIntegrations',
  retention: 'adminSettings:subTabRetention',
  'system-info': 'adminSettings:subTabSystemInfo',
  authentication: 'adminSettings:subTabAuthentication',
  'logging-telemetry': 'adminSettings:subTabLoggingTelemetry',
  security: 'adminSettings:subTabSecurity',
  performance: 'adminSettings:subTabPerformance',
  'db-admin': 'adminSettings:subTabDbAdmin',
};

function pageDefaultTo(path: string): string {
  if (path === '/settings') return '/settings?tab=personal';
  if (path === '/alerts') return '/alerts?tab=system';
  if (path === '/backup') return '/backup?tab=backup';
  if (path === '/metrics') return '/metrics/mission-control';
  return path;
}

/** Resolve the visible route hierarchy without duplicating the sidebar's page/group mapping. */
export function resolveBreadcrumbs(
  pathname: string,
  search: string,
  role: string | null,
): BreadcrumbItem[] {
  const visibleGroups = navGroups
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => !item.adminOnly || role === 'Admin'),
    }))
    .filter((group) => group.items.length > 0);
  const candidates = visibleGroups.flatMap((group) => group.items.map((item) => ({ group, item })));
  const match = candidates
    .filter(({ item }) => item.to === pathname || (item.to !== '/' && pathname.startsWith(`${item.to}/`)))
    .sort((a, b) => b.item.to.length - a.item.to.length)[0]
    ?? candidates.find(({ item }) => item.to === '/' && pathname === '/');
  if (!match) return [];

  const groupTarget = pageDefaultTo(match.group.items[0].to);
  const pageTarget = pageDefaultTo(match.item.to);
  const crumbs: BreadcrumbItem[] = [
    { labelKey: `nav:${match.group.labelKey}`, to: groupTarget },
    { labelKey: `nav:${match.item.key}` },
  ];
  const params = new URLSearchParams(search);

  if (match.item.to === '/settings') {
    crumbs[1].to = pageTarget;
    const system = role === 'Admin' && params.get('tab') === 'system';
    if (!system) {
      crumbs.push({ labelKey: 'adminSettings:tabPersonal' });
      return crumbs;
    }
    const requested = params.get('section') ?? 'integrations';
    const section = settingsSectionKeys[requested] ? requested : 'integrations';
    crumbs.push({ labelKey: 'adminSettings:tabSystem', to: '/settings?tab=system&section=integrations' });
    crumbs.push({ labelKey: settingsSectionKeys[section] });
    return crumbs;
  }

  if (match.item.to === '/alerts') {
    crumbs[1].to = pageTarget;
    const custom = params.get('tab') === 'custom';
    crumbs.push({ labelKey: custom ? 'alerts:system.tabCustom' : 'alerts:system.tabSystem' });
    return crumbs;
  }

  if (match.item.to === '/backup') {
    crumbs[1].to = pageTarget;
    const restore = params.get('tab') === 'restore';
    crumbs.push({ labelKey: restore ? 'backup:tabs.restore' : 'backup:tabs.backup' });
    return crumbs;
  }

  if (match.item.to === '/metrics') {
    crumbs[1].to = pageTarget;
    const sectionId = pathname.split('/')[2] ?? 'mission-control';
    const section = metricsSections.find((entry) => entry.id === sectionId) ?? metricsSections[0];
    crumbs.push({ labelKey: `metrics:sectionLabels.${section.labelKey}` });
  }

  return crumbs;
}

