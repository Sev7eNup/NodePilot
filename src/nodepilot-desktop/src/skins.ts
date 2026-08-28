import { existsSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Window- and tray-icon selection for the shell, following the color skin picked in the SPA.
 * `applyFavicon` (nodepilot-ui/src/lib/appIcon.ts) writes `/appicon-<skin>.png` into the SPA's
 * `<link rel="icon">`, and Chromium reports that to the main process as `page-favicon-updated`,
 * so the shell needs no preload script or IPC channel. Skin ids are not mirrored here: the files
 * `scripts/generate-desktop-icons.ps1` emits under `assets/skins/` decide which skins resolve.
 */

/** Skin ids come from the renderer and end up in a path join, so they must match the strict
 *  charset the icon generator enforces ($SKIN_ID there). */
const SKIN_ID = /^[a-z][a-z0-9-]{0,31}$/;

/** The `appicon-<skin>.png` file-name shape that `APP_ICON_BY_SKIN` produces. */
const FAVICON_NAME = /^appicon-([a-z][a-z0-9-]{0,31})\.png$/;

export interface SkinIcons {
  /** 256px window icon for the title bar, Alt+Tab and the taskbar. */
  window: string;
  /** 32px notification-area icon. */
  tray: string;
}

/**
 * The build-time default pair, rendered from the default skin's brand asset. Used for every
 * window until the SPA reports its skin, and for the exe, installer and Start-Menu entry, which
 * cannot follow a skin at all.
 */
export function defaultIcons(assetsDir: string): SkinIcons {
  return { window: join(assetsDir, 'icon.png'), tray: join(assetsDir, 'tray.png') };
}

/** The skin id encoded in a SPA favicon URL, or null when the URL is not a skin favicon. */
export function skinFromFaviconUrl(url: string): string | null {
  let name: string;
  try {
    name = new URL(url).pathname.split('/').pop() ?? '';
  } catch {
    return null;
  }
  return FAVICON_NAME.exec(name)?.[1] ?? null;
}

/**
 * The generated icon pair for a skin, or null when this build has none: a dev run straight from
 * source (assets/ is generated, not committed) or a skin newer than the icon set. Callers keep
 * the icon they already show in that case.
 */
export function skinIcons(
  assetsDir: string,
  skin: string,
  exists: (path: string) => boolean = existsSync,
): SkinIcons | null {
  if (!SKIN_ID.test(skin)) return null;
  const icons: SkinIcons = {
    window: join(assetsDir, 'skins', `${skin}.png`),
    tray: join(assetsDir, 'skins', `${skin}-tray.png`),
  };
  return exists(icons.window) && exists(icons.tray) ? icons : null;
}

/**
 * Resolves the icon pair for a `page-favicon-updated` payload. Chromium may report several
 * favicons; the first that resolves to a generated skin set wins.
 */
export function skinIconsForFavicons(
  assetsDir: string,
  favicons: readonly string[],
  exists: (path: string) => boolean = existsSync,
): SkinIcons | null {
  for (const url of favicons) {
    const skin = skinFromFaviconUrl(url);
    if (!skin) continue;
    const icons = skinIcons(assetsDir, skin, exists);
    if (icons) return icons;
  }
  return null;
}
