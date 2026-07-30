import { existsSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Window- and tray-icon selection for the shell, following the color skin the user picked in the
 * SPA.
 *
 * The signal is the SPA's own favicon: `applyFavicon` (nodepilot-ui/src/lib/appIcon.ts) writes
 * `/appicon-<skin>.png` into `<link rel="icon">` on every skin change, and Chromium reports that
 * to the main process as `page-favicon-updated`. Reusing it keeps the production SPA window
 * preload-less and IPC-free — the shell reads a one-way signal the renderer already broadcasts
 * instead of being handed a channel that could be talked into doing more.
 *
 * The skin ids are deliberately NOT mirrored here. `scripts/generate-desktop-icons.ps1` emits one
 * `assets/skins/<skin>.png` per `appicon-<skin>.png` the SPA ships, so "which skins exist" is
 * answered by the files on disk. A skin added to the UI without a regenerated icon set degrades
 * to keeping the current icon — never to a wrong one.
 */

/** Skin ids arrive from the renderer and end up in a path join, so they are held to the same
 *  strict charset the generator enforces (see $SKIN_ID there). */
const SKIN_ID = /^[a-z][a-z0-9-]{0,31}$/;

/** `appicon-<skin>.png` — the file-name shape `APP_ICON_BY_SKIN` produces. */
const FAVICON_NAME = /^appicon-([a-z][a-z0-9-]{0,31})\.png$/;

export interface SkinIcons {
  /** 256px window icon — title bar, Alt+Tab, taskbar. */
  window: string;
  /** 32px notification-area icon. */
  tray: string;
}

/**
 * The build-time default pair. Blue by design: the generator renders it from the default skin's
 * brand asset, not from the untinted orange source art. Applied to every window until the SPA
 * reports its skin, and to the exe/installer/Start-Menu entry, which cannot follow a skin at all.
 */
export function defaultIcons(assetsDir: string): SkinIcons {
  return { window: join(assetsDir, 'icon.png'), tray: join(assetsDir, 'tray.png') };
}

/** The skin id encoded in a SPA favicon URL, or null if the URL is not one of ours. */
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
 * The generated icon pair for a skin, or null when this build has none — a dev run straight from
 * source (assets/ is generated, not committed) or a skin newer than the icon set. Callers keep
 * whatever icon they already show in that case.
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
