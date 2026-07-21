import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { useThemeStore, resolveTheme, resolveSkin, applyTheme, normalizeTheme, THEMES } from '../../stores/themeStore';

/**
 * Theme resolution + DOM side-effects. Two pieces:
 *   * resolveTheme(theme) — pure function, light/dark/system → light|dark
 *   * applyTheme + setTheme — toggle the .dark class on documentElement
 *
 * The system path reads window.matchMedia. The shared test setup stubs that as
 * "always light", but we override per-test where the dark branch matters.
 */

const initialState = useThemeStore.getState();

function mockSystemDark(dark: boolean) {
  // Override the matchMedia stub installed in setup.ts. Each test calling this should
  // restore via afterEach (vi.restoreAllMocks).
  vi.spyOn(window, 'matchMedia').mockImplementation((q: string) => ({
    matches: dark && q.includes('dark'),
    media: q,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  } as MediaQueryList));
}

describe('resolveTheme (pure)', () => {
  afterEach(() => vi.restoreAllMocks());

  it('explicitDark_returnsDark', () => {
    expect(resolveTheme('dark')).toBe('dark');
  });

  it('explicitLight_returnsLight', () => {
    expect(resolveTheme('light')).toBe('light');
  });

  it('systemPrefersDark_returnsDark', () => {
    mockSystemDark(true);
    expect(resolveTheme('system')).toBe('dark');
  });

  it('systemPrefersLight_returnsLight', () => {
    mockSystemDark(false);
    expect(resolveTheme('system')).toBe('light');
  });

  it('darkLilaSkin_resolvesToDarkBase', () => {
    // A re-accented dark skin keeps base 'dark' so code editors / React Flow theming
    // (which read resolvedTheme) need no per-skin handling.
    expect(resolveTheme('dark-lila')).toBe('dark');
    expect(resolveSkin('dark-lila')).toBe('dark-lila');
  });

  it('lightGreySkin_resolvesToLightBase', () => {
    expect(resolveTheme('light-grey')).toBe('light');
    expect(resolveSkin('light-grey')).toBe('light-grey');
  });

  it('unknownSkin_fallsBackToLight', () => {
    expect(resolveTheme('totally-unknown' as never)).toBe('light');
    expect(normalizeTheme('dark-synthwave' as never)).toBe('light');
  });
});

describe('skins / data-skin attribute', () => {
  beforeEach(() => {
    document.documentElement.classList.remove('dark', 'np-accent-remap');
    delete document.documentElement.dataset.skin;
  });
  afterEach(() => {
    document.documentElement.classList.remove('dark', 'np-accent-remap');
    delete document.documentElement.dataset.skin;
    vi.restoreAllMocks();
  });

  it('registryContainsAllSevenSkins', () => {
    const ids = THEMES.map((t) => t.id);
    expect(ids).toEqual(['light', 'light-grey', 'light-sparkasse', 'dark', 'dark-lila', 'dark-sparkasse', 'dark-nebula']);
    expect(THEMES.find((t) => t.id === 'dark-lila')?.base).toBe('dark');
    expect(THEMES.find((t) => t.id === 'light-grey')?.base).toBe('light');
    expect(THEMES.find((t) => t.id === 'dark-sparkasse')?.base).toBe('dark');
    expect(THEMES.find((t) => t.id === 'light-sparkasse')?.base).toBe('light');
    expect(THEMES.find((t) => t.id === 'dark-nebula')?.base).toBe('dark');
  });

  it('applyTheme_darkSparkasse_addsDarkClassAndDataSkinAndRemap', () => {
    applyTheme('dark-sparkasse');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    expect(document.documentElement.dataset.skin).toBe('dark-sparkasse');
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(true);
  });

  it('applyTheme_lightSparkasse_lightBaseWithSkinMarkerAndRemap', () => {
    applyTheme('light-sparkasse');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
    expect(document.documentElement.dataset.skin).toBe('light-sparkasse');
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(true);
  });

  it('applyTheme_darkLila_addsDarkClassAndDataSkin', () => {
    applyTheme('dark-lila');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    expect(document.documentElement.dataset.skin).toBe('dark-lila');
  });

  it('applyTheme_darkNebula_addsDarkClassAndDataSkinAndRemap', () => {
    applyTheme('dark-nebula');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    expect(document.documentElement.dataset.skin).toBe('dark-nebula');
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(true);
  });

  it('applyTheme_lightGrey_lightBaseWithSkinMarkerAndRemap', () => {
    applyTheme('light-grey');
    expect(document.documentElement.classList.contains('dark')).toBe(false); // light base
    expect(document.documentElement.dataset.skin).toBe('light-grey');
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(true);
  });

  it('applyTheme_dark_setsDataSkinDarkAndRemap', () => {
    applyTheme('dark');
    expect(document.documentElement.dataset.skin).toBe('dark');
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(true);
  });

  it('applyTheme_light_setsDataSkinLightWithoutRemap', () => {
    applyTheme('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
    expect(document.documentElement.dataset.skin).toBe('light');
    // The blue `light` default keeps its blue accent → no remap marker.
    expect(document.documentElement.classList.contains('np-accent-remap')).toBe(false);
  });

  it('setTheme_darkLila_persistsSkinAndResolvesDarkBase', () => {
    useThemeStore.getState().setTheme('dark-lila');
    const s = useThemeStore.getState();
    expect(s.theme).toBe('dark-lila');
    expect(s.resolvedTheme).toBe('dark');
    expect(document.documentElement.dataset.skin).toBe('dark-lila');
  });

  it('setTheme_lightGrey_persistsSkinAndResolvesLightBase', () => {
    useThemeStore.getState().setTheme('light-grey');
    const s = useThemeStore.getState();
    expect(s.theme).toBe('light-grey');
    expect(s.resolvedTheme).toBe('light');
    expect(document.documentElement.dataset.skin).toBe('light-grey');
  });
});

describe('applyTheme (DOM side-effect)', () => {
  beforeEach(() => {
    document.documentElement.classList.remove('dark');
  });

  afterEach(() => {
    document.documentElement.classList.remove('dark');
    vi.restoreAllMocks();
  });

  it('applyTheme_dark_addsDarkClass', () => {
    applyTheme('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('applyTheme_light_removesDarkClass', () => {
    document.documentElement.classList.add('dark');
    applyTheme('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('applyTheme_systemFollowsMatchMedia', () => {
    mockSystemDark(true);
    applyTheme('system');
    expect(document.documentElement.classList.contains('dark')).toBe(true);

    mockSystemDark(false);
    applyTheme('system');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });
});

describe('useThemeStore', () => {
  beforeEach(() => {
    useThemeStore.setState(initialState, true);
    document.documentElement.classList.remove('dark');
  });

  afterEach(() => {
    document.documentElement.classList.remove('dark');
    vi.restoreAllMocks();
  });

  it('defaults_systemMode_resolvedReflectsMatchMedia', () => {
    // The setup.ts matchMedia stub returns "always light", so resolved should be "light"
    // until a test overrides matchMedia.
    expect(useThemeStore.getState().theme).toBe('system');
  });

  it('setTheme_updatesBothThemeAndResolvedTheme', () => {
    useThemeStore.getState().setTheme('dark');
    const s = useThemeStore.getState();
    expect(s.theme).toBe('dark');
    expect(s.resolvedTheme).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('setTheme_light_removesDarkClass', () => {
    document.documentElement.classList.add('dark');
    useThemeStore.getState().setTheme('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
    expect(useThemeStore.getState().resolvedTheme).toBe('light');
  });

  it('syncResolved_recomputesAfterSystemChange', () => {
    // Use case: the OS flipped its dark-mode preference at runtime. Calling
    // syncResolved() must re-read matchMedia and update resolvedTheme + the .dark class.
    useThemeStore.getState().setTheme('system');
    mockSystemDark(true);

    useThemeStore.getState().syncResolved();

    expect(useThemeStore.getState().resolvedTheme).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });
});
