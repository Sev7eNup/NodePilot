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

/**
 * Blocks the window from navigating away from the configured origin and denies every popup or
 * new-window request. Only full navigations and window.open are intercepted, so the in-page
 * History API routing of the SPA keeps working.
 */
export function hardenWindow(win: BrowserWindow, allowedOrigin: string): void {
  const sameOrigin = (url: string): boolean => {
    try {
      return new URL(url).origin === allowedOrigin;
    } catch {
      return false;
    }
  };

  win.webContents.on('will-navigate', (event, url) => {
    if (!sameOrigin(url)) event.preventDefault();
  });
  win.webContents.on('will-redirect', (event, url) => {
    if (!sameOrigin(url)) event.preventDefault();
  });
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
}
