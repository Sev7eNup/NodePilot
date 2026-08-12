import { describe, it, expect, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { SupportLogViewerSection } from '../../../components/admin-settings/SupportLogViewerSection';

/**
 * The plain-text tail view sizes its window from a persisted, drag-resizable height. The
 * default grew from 640px to 832px so an operator sees ~30% more lines without dragging,
 * and the localStorage key moved `.v1` → `.v2` to make that new default actually reachable
 * in a browser that already stored a height. Both halves are behaviour, so both are pinned:
 * a silent revert of the key bump would leave every existing profile on the old window.
 */

const PLAIN_DEFAULT_HEIGHT = 832;
const V1_KEY = 'nodepilot.supportLog.plainHeight.v1';
const V2_KEY = 'nodepilot.supportLog.plainHeight.v2';

const server = setupServer(
  http.get('/api/diagnostics/support-log', () =>
    HttpResponse.json({ fileName: 'nodepilot-support-20260812.log', lineCount: 2, lines: ['line one', 'line two'] }),
  ),
);
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => globalThis.localStorage.clear());
afterEach(() => { server.resetHandlers(); globalThis.localStorage.clear(); });
afterAll(() => server.close());

/** Renders the section and switches to the plain-text tail view. */
async function renderPlainView() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utils = render(
    <QueryClientProvider client={qc}><SupportLogViewerSection /></QueryClientProvider>,
  );
  // The section opens on the DB table; the tail view is the second mode button.
  fireEvent.click(screen.getByRole('button', { name: /plain-text/i }));
  const pre = await waitFor(() => {
    const el = utils.container.querySelector('pre');
    if (!el) throw new Error('tail view <pre> not mounted');
    return el as HTMLPreElement;
  });
  return pre;
}

describe('SupportLogViewerSection — plain-text window height', () => {
  it('defaultsToTheLargerWindow_whenNothingIsPersisted', async () => {
    const pre = await renderPlainView();
    expect(pre.style.height).toBe(`${PLAIN_DEFAULT_HEIGHT}px`);
  });

  it('honoursAPersistedV2Height', async () => {
    globalThis.localStorage.setItem(V2_KEY, '900');
    const pre = await renderPlainView();
    expect(pre.style.height).toBe('900px');
  });

  it('ignoresTheStaleV1Height_soTheNewDefaultWins', async () => {
    // A browser that used the old viewer has 640 sitting under the v1 key. Reading it back
    // would defeat the whole point of raising the default.
    globalThis.localStorage.setItem(V1_KEY, '640');
    const pre = await renderPlainView();
    expect(pre.style.height).toBe(`${PLAIN_DEFAULT_HEIGHT}px`);
  });

  it('ignoresAPersistedHeightBelowTheMinimum', async () => {
    // Guards the `n >= PLAIN_MIN_HEIGHT` read-back: a corrupt or hand-edited tiny value must
    // not collapse the window to an unusable sliver.
    globalThis.localStorage.setItem(V2_KEY, '12');
    const pre = await renderPlainView();
    expect(pre.style.height).toBe(`${PLAIN_DEFAULT_HEIGHT}px`);
  });
});
