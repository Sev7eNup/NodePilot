import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type ThemeBase = 'light' | 'dark';

/**
 * A selectable color scheme ("skin").
 *
 * - `base` decides whether the `dark` class goes on <html> and what `resolvedTheme`
 *   reports — the only thing code editors (Monaco/CodeMirror) and React Flow care
 *   about. So a purely re-accented dark skin (e.g. `dark-lila`) keeps `base: 'dark'`
 *   and needs ZERO changes in those consumers.
 * - `id` doubles as the `data-skin` attribute written to <html>; index.css keys
 *   skin-specific accent/surface overrides on `html.dark[data-skin="<id>"]`.
 *
 * ▶ To add another scheme: append one entry here, add one
 *   `html.dark[data-skin="<id>"] …` block in index.css, one i18n label
 *   (`settings`/`nav` namespaces), and one accent hue in
 *   `scripts/generate-logo-skins.py` + a `LOGO_BY_SKIN` entry in `BrandLogo.tsx`
 *   (re-run the script to emit `appicon-<id>.png`). Settings + the sidebar render
 *   it automatically.
 */
export interface ThemeDef {
  id: string;
  base: ThemeBase;
  /** i18n key (present in both the `settings` and `nav` namespaces). */
  labelKey: string;
  /** When the accent isn't blue, opt the skin into the `np-accent-remap` marker class
   *  so index.css remaps hardcoded Tailwind blues → the skin accent. The blue `light`
   *  default omits it. */
  remapBlue?: boolean;
}

// Display order (left→right in the Settings picker, and the sidebar cycle/popover): all
// light skins first, then all dark skins, each running default → tinted → bank. `system`
// is appended by consumers, so it always lands last.
export const THEMES = [
  { id: 'light', base: 'light', labelKey: 'themeLight' },
  { id: 'light-grey', base: 'light', labelKey: 'themeLightGrey', remapBlue: true },
  { id: 'light-sparkasse', base: 'light', labelKey: 'themeSparkasseLight', remapBlue: true },
  { id: 'dark', base: 'dark', labelKey: 'themeDark', remapBlue: true },
  { id: 'dark-lila', base: 'dark', labelKey: 'themeDarkLila', remapBlue: true },
  { id: 'dark-sparkasse', base: 'dark', labelKey: 'themeSparkasseDark', remapBlue: true },
  { id: 'dark-nebula', base: 'dark', labelKey: 'themeDarkNebula', remapBlue: true },
] as const satisfies readonly ThemeDef[];

export type SkinId = (typeof THEMES)[number]['id'];
/** A persisted preference: a concrete skin id or the OS-following `system`. */
export type Theme = SkinId | 'system';

interface ThemeStore {
  theme: Theme;
  resolvedTheme: ThemeBase;
  setTheme: (t: Theme) => void;
  syncResolved: () => void;
}

function isDarkSystem() {
  return typeof window !== 'undefined' && globalThis.matchMedia('(prefers-color-scheme: dark)').matches;
}

function skinDef(id: string): ThemeDef | undefined {
  return THEMES.find((t) => t.id === id);
}

/** Normalize stale persisted values after a skin is removed from the registry. */
export function normalizeTheme(theme: Theme): Theme {
  return theme === 'system' || skinDef(theme) ? theme : 'light';
}

/** Concrete skin actually applied — resolves `system` to `light`/`dark`, and falls
 *  back to `light` for any unknown/stale persisted value. */
export function resolveSkin(theme: Theme): SkinId {
  if (theme === 'system') return isDarkSystem() ? 'dark' : 'light';
  return (skinDef(theme)?.id as SkinId | undefined) ?? 'light';
}

/** The light/dark base of the applied skin — what `resolvedTheme` exposes. */
export function resolveTheme(theme: Theme): ThemeBase {
  return skinDef(resolveSkin(theme))!.base;
}

export function applyTheme(theme: Theme) {
  const skin = resolveSkin(theme);
  const def = skinDef(skin)!;
  const root = document.documentElement;
  root.classList.toggle('dark', def.base === 'dark');
  root.classList.toggle('np-accent-remap', !!def.remapBlue);
  root.dataset.skin = skin;
}

export const useThemeStore = create<ThemeStore>()(
  persist(
    (set, get) => ({
      theme: 'system',
      resolvedTheme: resolveTheme('system'),
      setTheme: (t) => {
        set({ theme: t, resolvedTheme: resolveTheme(t) });
        applyTheme(t);
      },
      syncResolved: () => {
        const t = get().theme;
        set({ resolvedTheme: resolveTheme(t) });
        applyTheme(t);
      },
    }),
    {
      name: 'nodepilot.theme',
      partialize: (s) => ({ theme: s.theme }),
      onRehydrateStorage: () => (state) => {
        if (!state) return;
        const normalizedTheme = normalizeTheme(state.theme);
        if (normalizedTheme !== state.theme) {
          state.setTheme(normalizedTheme);
          return;
        }
        state.theme = normalizedTheme;
        state.resolvedTheme = resolveTheme(state.theme);
        applyTheme(state.theme);
      },
    },
  ),
);
