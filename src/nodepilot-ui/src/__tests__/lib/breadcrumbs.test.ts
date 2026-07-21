import { describe, expect, it } from 'vitest';
import { resolveBreadcrumbs } from '../../lib/breadcrumbs';

describe('resolveBreadcrumbs', () => {
  it('maps a simple route to its sidebar group and page', () => {
    expect(resolveBreadcrumbs('/', '', 'Admin')).toEqual([
      { labelKey: 'nav:groupWorkspace', to: '/' },
      { labelKey: 'nav:dashboard' },
    ]);
  });

  it('uses the first role-visible administration page as the group target', () => {
    expect(resolveBreadcrumbs('/settings', '?tab=personal', 'Admin')[0].to).toBe('/users');
    expect(resolveBreadcrumbs('/settings', '?tab=personal', 'Operator')[0].to)
      .toBe('/settings?tab=personal');
  });

  it('includes personal and nested system settings views', () => {
    expect(resolveBreadcrumbs('/settings', '?tab=personal', 'Admin').map((item) => item.labelKey))
      .toEqual(['nav:groupAdmin', 'nav:settings', 'adminSettings:tabPersonal']);
    expect(resolveBreadcrumbs('/settings', '?tab=system&section=security', 'Admin')).toEqual([
      { labelKey: 'nav:groupAdmin', to: '/users' },
      { labelKey: 'nav:settings', to: '/settings?tab=personal' },
      { labelKey: 'adminSettings:tabSystem', to: '/settings?tab=system&section=integrations' },
      { labelKey: 'adminSettings:subTabSecurity' },
    ]);
  });

  it('falls back to personal settings for non-admins', () => {
    expect(resolveBreadcrumbs('/settings', '?tab=system&section=security', 'Viewer').at(-1)?.labelKey)
      .toBe('adminSettings:tabPersonal');
  });

  it.each([
    ['/alerts', '?tab=custom', 'alerts:system.tabCustom'],
    ['/backup', '?tab=restore', 'backup:tabs.restore'],
    ['/metrics/security', '', 'metrics:sectionLabels.security'],
  ])('resolves the primary sub-view for %s', (pathname, search, expected) => {
    expect(resolveBreadcrumbs(pathname, search, 'Admin').at(-1)?.labelKey).toBe(expected);
  });

  it('returns no crumbs for an unknown route', () => {
    expect(resolveBreadcrumbs('/not-found', '', 'Admin')).toEqual([]);
  });
});
