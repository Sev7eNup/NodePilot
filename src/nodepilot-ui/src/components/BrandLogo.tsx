import { useThemeStore } from '../stores/themeStore';
import { appIconForTheme } from '../lib/appIcon';

/**
 * The NodePilot brand mark, recolored to match the active color skin. Subscribes to the
 * theme store so it re-renders (and swaps the asset) the moment the user switches skin.
 * `system` resolves to the light/dark variant via the store's resolvedTheme. Shares the
 * per-skin icon map with `applyFavicon` (see lib/appIcon.ts) so the in-app logo and the
 * browser tab icon always recolor in lockstep.
 */
export function BrandLogo({ className, alt = 'NodePilot' }: Readonly<{ className?: string; alt?: string }>) {
  const theme = useThemeStore((s) => s.theme);
  const resolvedTheme = useThemeStore((s) => s.resolvedTheme);
  return <img src={appIconForTheme(theme, resolvedTheme)} alt={alt} className={`object-contain ${className ?? ''}`} />;
}