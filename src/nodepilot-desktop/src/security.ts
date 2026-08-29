import { X509Certificate } from 'node:crypto';
import type { BrowserWindow, Session } from 'electron';

/**
 * Pins the loopback server certificate by SHA-256 fingerprint and locks the session down
 * (no permissions, no downloads). No system root CA is installed, so this is the only place
 * where trust is decided.
 */
export function hardenSession(sess: Session, pinnedSha256: string): void {
  sess.setCertificateVerifyProc((request, callback) => {
    const host = request.hostname;
    if (host === 'localhost' || host === '127.0.0.1') {
      try {
        const actual = new X509Certificate(request.certificate.data)
          .fingerprint256.replace(/:/g, '')
          .toUpperCase();
        callback(actual === pinnedSha256 ? 0 : -2);
      } catch {
        callback(-2);
      }
      return;
    }
    // No other host is expected; if one is reached, fall back to Chromium's own verification.
    callback(-3);
  });

  sess.setPermissionRequestHandler((_wc, _permission, done) => done(false));
  sess.setPermissionCheckHandler(() => false);
  sess.on('will-download', (event) => event.preventDefault());
}

/** Request path the API serves the bundled documentation SPA under. */
const DOCS_PATH = '/docs';

/**
 * What a window is allowed to show. An `app` window may go anywhere on the NodePilot origin; a
 * `docs` window is additionally pinned to the documentation, so a redirect or a crafted link
 * cannot turn it into a second, chrome-less view of the application.
 */
export type WindowScope = 'app' | 'docs';

export interface HardenWindowOptions {
  /** What the window may show. Defaults to `app`. */
  scope?: WindowScope;
  /**
   * Hands a validated https link to the system browser. Injected rather than imported so this
   * module stays free of a live Electron runtime; main.ts passes `shell.openExternal`. Without
   * it, external links are simply dropped.
   */
  openExternal?: (url: string) => void;
}

function isDocsUrl(url: string, allowedOrigin: string): boolean {
  try {
    const { origin, pathname } = new URL(url);
    // Exact path or a child of it — `/docsomething` must not pass.
    return origin === allowedOrigin && (pathname === DOCS_PATH || pathname.startsWith(`${DOCS_PATH}/`));
  } catch {
    return false;
  }
}

/** Links the shell is willing to hand to the system browser. Scheme allow-list, not a deny-list. */
function isExternalLink(url: string): boolean {
  try {
    return new URL(url).protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Contains a window: it may only navigate within what its scope allows, and it never opens a
 * window onto anything else. Only full navigations and window.open are intercepted, so the
 * in-page History API routing of the SPA keeps working.
 *
 * The one popup that is allowed is the bundled documentation at /docs, opened from an app
 * window. It gets its own window because the shell has no menu bar and no back gesture —
 * navigating the single app window into the docs would strand the user. The child is created
 * with its own sandboxed options and is then hardened with the `docs` scope, which pins it to
 * /docs for navigations *and* redirects.
 *
 * External https links (the docs link out to GitHub, releases and SECURITY.md) are handed to the
 * system browser and the window request is still denied, so the shell never renders foreign
 * content itself. Everything else — other schemes, other origins — is dropped.
 */
export function hardenWindow(
  win: BrowserWindow,
  allowedOrigin: string,
  options: HardenWindowOptions = {},
): void {
  const { scope = 'app', openExternal } = options;

  const sameOrigin = (url: string): boolean => {
    try {
      return new URL(url).origin === allowedOrigin;
    } catch {
      return false;
    }
  };

  const mayNavigateTo = (url: string): boolean =>
    scope === 'docs' ? isDocsUrl(url, allowedOrigin) : sameOrigin(url);

  win.webContents.on('will-navigate', (event, url) => {
    if (!mayNavigateTo(url)) event.preventDefault();
  });
  win.webContents.on('will-redirect', (event, url) => {
    if (!mayNavigateTo(url)) event.preventDefault();
  });

  win.webContents.setWindowOpenHandler(({ url }) => {
    // Only an app window may spawn the docs window; a docs window cannot spawn another one.
    if (scope === 'app' && isDocsUrl(url, allowedOrigin)) {
      return {
        action: 'allow',
        // Set at creation time — did-create-window fires only after the window already exists,
        // so these cannot be applied afterwards.
        overrideBrowserWindowOptions: {
          autoHideMenuBar: true,
          webPreferences: {
            sandbox: true,
            contextIsolation: true,
            nodeIntegration: false,
            webviewTag: false,
          },
        },
      };
    }
    if (isExternalLink(url)) openExternal?.(url);
    return { action: 'deny' };
  });

  win.webContents.on('did-create-window', (child) => {
    child.removeMenu();
    hardenWindow(child, allowedOrigin, { scope: 'docs', openExternal });
  });
}
