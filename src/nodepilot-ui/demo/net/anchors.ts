/**
 * Keeps anchor navigation inside the demo.
 *
 * The backend is a `fetch` patch, and a plain `<a href="/api/…">` never goes through `fetch` —
 * it is a document navigation. On a sub-path deployment that lands the visitor on the host's
 * own 404, outside the demo, and the in-memory world is gone when they come back.
 *
 * The audit-log export is the case that exists today (two anchors carrying
 * `/api/audit/export?format=…`), but this guards the class rather than that one page: any
 * same-origin link that would leave the demo's own directory is routed through the demo
 * instead, and a response that carries `Content-Disposition` is saved as a real download.
 */

let detach: (() => void) | null = null;

/** Directory the demo is served from, e.g. "/NodePilot/demo/". */
function demoBasePath(): string {
  return new URL('./', document.baseURI).pathname;
}

function isPlainLeftClick(event: MouseEvent): boolean {
  return event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
}

async function saveAsDownload(response: Response, fallbackName: string): Promise<void> {
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const disposition = response.headers.get('content-disposition') ?? '';
  const named = /filename="?([^";]+)"?/i.exec(disposition);

  const link = document.createElement('a');
  link.href = url;
  link.download = named?.[1] ?? fallbackName;
  document.body.append(link);
  link.click();
  link.remove();
  // Revoke on the next task so the browser has started reading the blob.
  globalThis.setTimeout(() => URL.revokeObjectURL(url), 0);
}

async function handleEscapingClick(event: MouseEvent, href: string): Promise<void> {
  event.preventDefault();
  try {
    const response = await fetch(href);
    if (response.ok && response.headers.has('content-disposition')) {
      await saveAsDownload(response, 'nodepilot-export');
      return;
    }
    console.warn(`[demo] blocked navigation that would leave the demo: ${href}`);
  } catch (error) {
    console.warn(`[demo] blocked navigation that would leave the demo: ${href}`, error);
  }
}

/** Installs the listener. Safe to call twice. */
export function installAnchorGuard(): void {
  if (detach) return;

  const onClick = (event: MouseEvent) => {
    if (!isPlainLeftClick(event)) return;
    const anchor = (event.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
    if (!anchor) return;
    // An explicit new tab to another site is intentional (repository, releases, the docs).
    if (anchor.target && anchor.target !== '_self') return;

    const resolved = new URL(anchor.href, document.baseURI);
    // blob:/data: links are in-page downloads (workflow export); they never leave the demo.
    if (resolved.protocol === 'blob:' || resolved.protocol === 'data:') return;
    if (resolved.origin !== globalThis.location.origin) return;
    if (resolved.pathname.startsWith(demoBasePath())) return;

    void handleEscapingClick(event, resolved.toString());
  };

  document.addEventListener('click', onClick, true);
  detach = () => document.removeEventListener('click', onClick, true);
}

/** Removes the listener. Used by tests. */
export function uninstallAnchorGuard(): void {
  detach?.();
  detach = null;
}
