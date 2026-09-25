import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { installAnchorGuard, uninstallAnchorGuard } from '../../../demo/net/anchors';

function clickAnchor(href: string): MouseEvent {
  const a = document.createElement('a');
  a.href = href;
  document.body.append(a);
  const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
  a.dispatchEvent(event);
  a.remove();
  return event;
}

// The demo is served from a sub-directory (GitHub Pages /demo/); links outside it are guarded.
beforeEach(() => {
  const base = document.createElement('base');
  base.href = `${location.origin}/demo/`;
  document.head.append(base);
});

afterEach(() => {
  uninstallAnchorGuard();
  vi.restoreAllMocks();
  document.head.querySelectorAll('base').forEach((b) => b.remove());
});

describe('demo anchor guard', () => {
  it('anchorGuard_BlobDownload_IsNotIntercepted', () => {
    installAnchorGuard();
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    // The workflow export saves through a blob: link; blocking it made Export do nothing in the demo.
    const event = clickAnchor(`blob:${location.origin}/2f1c0d7e-0000-4000-8000-000000000000`);
    expect(event.defaultPrevented).toBe(false);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('anchorGuard_DataUrl_IsNotIntercepted', () => {
    installAnchorGuard();
    const event = clickAnchor('data:application/json,%7B%7D');
    expect(event.defaultPrevented).toBe(false);
  });

  it('anchorGuard_SameOriginApiLink_IsStillRoutedThroughTheDemo', () => {
    installAnchorGuard();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}', { status: 404 }));
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const event = clickAnchor(`${location.origin}/api/audit/export?format=csv`);
    expect(event.defaultPrevented).toBe(true);
  });
});
