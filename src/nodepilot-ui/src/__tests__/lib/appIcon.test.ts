import { describe, it, expect, beforeEach } from 'vitest';
import { applyFavicon, appIconForTheme, APP_ICON_BY_SKIN } from '../../lib/appIcon';
import { useThemeStore } from '../../stores/themeStore';

describe('appIcon', () => {
  beforeEach(() => {
    document.head.innerHTML = '<link rel="icon" type="image/png" href="/appicon.png" />';
    useThemeStore.setState({ theme: 'light', resolvedTheme: 'light' });
  });

  it.each([
    ['light', 'light', '/appicon-light.png'],
    ['dark', 'dark', '/appicon-dark.png'],
    ['dark-lila', 'dark', '/appicon-dark-lila.png'],
    ['light-grey', 'light', '/appicon-light-grey.png'],
    ['dark-sparkasse', 'dark', '/appicon-dark-sparkasse.png'],
    ['light-sparkasse', 'light', '/appicon-light-sparkasse.png'],
    ['dark-nebula', 'dark', '/appicon-dark-nebula.png'],
  ])('resolves the %s skin icon', (theme, resolved, expected) => {
    expect(appIconForTheme(theme as never, resolved as never)).toBe(expected);
  });

  it('follows the resolved base under the system theme', () => {
    expect(appIconForTheme('system', 'light')).toBe('/appicon-light.png');
    expect(appIconForTheme('system', 'dark')).toBe('/appicon-dark.png');
  });

  it('falls back to the light icon for a removed persisted skin', () => {
    expect(appIconForTheme('dark-synthwave' as never, 'dark')).toBe('/appicon-light.png');
  });

  it('covers every declared skin', () => {
    // Guards the favicon + logo from drifting apart when a new skin is added.
    for (const id of Object.keys(APP_ICON_BY_SKIN)) {
      expect(APP_ICON_BY_SKIN[id as keyof typeof APP_ICON_BY_SKIN]).toMatch(/^\/appicon-.*\.png$/);
    }
  });

  it('writes the resolved icon into the existing <link rel="icon">', () => {
    applyFavicon('dark-lila', 'light');
    const link = document.querySelector('link[rel="icon"]') as HTMLLinkElement;
    expect(link.getAttribute('href')).toBe('/appicon-dark-lila.png');
    expect(link.rel).toBe('icon');
  });

  it('creates a new link element when none exists', () => {
    document.head.innerHTML = '';
    applyFavicon('dark', 'dark');
    const link = document.querySelector('link[rel="icon"]') as HTMLLinkElement;
    expect(link).not.toBeNull();
    expect(link.getAttribute('href')).toBe('/appicon-dark.png');
    expect(link.type).toBe('image/png');
  });

  it('is idempotent — does not rewrite an already-matching href', () => {
    applyFavicon('light', 'light');
    const link = document.querySelector('link[rel="icon"]') as HTMLLinkElement;
    expect(link.getAttribute('href')).toBe('/appicon-light.png');
    const before = link;
    applyFavicon('light', 'light');
    expect(document.querySelector('link[rel="icon"]')).toBe(before);
    expect(link.getAttribute('href')).toBe('/appicon-light.png');
  });

  it('swaps the href when the skin changes', () => {
    applyFavicon('light', 'light');
    applyFavicon('dark-sparkasse', 'dark');
    expect(
      (document.querySelector('link[rel="icon"]') as HTMLLinkElement).getAttribute('href'),
    ).toBe('/appicon-dark-sparkasse.png');
  });
});
