import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { AuditLogPage } from '../../pages/AuditLogPage';

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const ENTRIES = [
  {
    id: 'e1',
    timestamp: '2026-04-25T12:00:00Z',
    userId: '11111111-1111-1111-1111-111111111111',
    username: 'alice',
    action: 'WORKFLOW_CREATED',
    resourceType: 'Workflow',
    resourceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    details: '{"name":"Test"}',
    ipAddress: '10.0.0.1',
  },
  {
    id: 'e2',
    timestamp: '2026-04-25T11:00:00Z',
    userId: null,
    username: null,
    action: 'LOGIN_FAILED',
    resourceType: 'User',
    resourceId: null,
    details: null,
    ipAddress: null,
  },
];

// The controller wraps rows in {items, nextCursor} — helper so each test stays terse.
function page(items: unknown[], nextCursor: { timestamp: string; id: string } | null = null) {
  return { items, nextCursor };
}

function renderPage() {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AuditLogPage />
    </QueryClientProvider>
  );
}

describe('AuditLogPage', () => {
  it('rendersFilters', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page([]))));
    renderPage();
    // The page title now lives in the app TopBar; assert the filter row renders instead.
    expect(await screen.findByPlaceholderText('WORKFLOW_CREATED')).toBeInTheDocument();
  });

  it('emptyResponse_showsEmptyMessage', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page([]))));
    renderPage();
    await waitFor(() => expect(screen.getByText(/No audit entries/)).toBeInTheDocument());
  });

  it('rendersOneRowPerEntry', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    // Each action appears twice: once as a quick-filter chip, once in its row.
    await waitFor(() => expect(screen.getAllByText('WORKFLOW_CREATED').length).toBeGreaterThanOrEqual(1));
    expect(screen.getAllByText('LOGIN_FAILED').length).toBeGreaterThanOrEqual(1);
  });

  it('rendersQuickFilterChipsFromActionCounts', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    await waitFor(() => expect(screen.getByText('Quick filter:')).toBeInTheDocument());
    // Both actions show up as chips with their (1) count
    expect(screen.getAllByText(/\(1\)/).length).toBeGreaterThanOrEqual(2);
  });

  it('quickFilterChip_setsActionFilter', async () => {
    let lastUrl = '';
    server.use(
      http.get(`${BASE}/api/audit`, ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(page(ENTRIES));
      })
    );
    renderPage();

    await waitFor(() => expect(screen.getAllByText('WORKFLOW_CREATED').length).toBeGreaterThanOrEqual(2));

    // Find the quick-filter chip — its containing button has class "font-mono px-1.5".
    const chips = screen.getAllByText('WORKFLOW_CREATED');
    const chipButton = chips.find((el) => {
      const btn = el.closest('button');
      return btn && btn.className.includes('px-1.5');
    })?.closest('button');
    fireEvent.click(chipButton!);

    await waitFor(() => expect(lastUrl).toContain('action=WORKFLOW_CREATED'));
  });

  it('typingInActionFilter_updatesQuery', async () => {
    let lastUrl = '';
    server.use(
      http.get(`${BASE}/api/audit`, ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(page([]));
      })
    );
    renderPage();

    await waitFor(() => expect(screen.getByPlaceholderText('WORKFLOW_CREATED')).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText('WORKFLOW_CREATED'), { target: { value: 'LOGIN_SUCCESS' } });

    await waitFor(() => expect(lastUrl).toContain('action=LOGIN_SUCCESS'));
  });

  it('expandingEntry_showsPrettyJsonDetails', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    await waitFor(() => expect(screen.getAllByText('WORKFLOW_CREATED').length).toBeGreaterThan(0));

    // The expandable row button is the one whose className contains the row-grid layout.
    const cells = screen.getAllByText('WORKFLOW_CREATED');
    const rowButton = cells.find((el) => {
      const btn = el.closest('button');
      return btn && btn.className.includes('grid');
    })?.closest('button');
    fireEvent.click(rowButton!);

    await waitFor(() => expect(screen.getByText(/"name": "Test"/)).toBeInTheDocument());
  });

  it('entryWithoutDetails_isNotExpandable', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    await waitFor(() => expect(screen.getAllByText('LOGIN_FAILED').length).toBeGreaterThan(0));

    const cells = screen.getAllByText('LOGIN_FAILED');
    const rowButton = cells.find((el) => {
      const btn = el.closest('button');
      return btn && btn.className.includes('grid');
    })?.closest('button');
    expect(rowButton).toBeDisabled();
  });

  it('refreshButton_triggersRefetch', async () => {
    let calls = 0;
    server.use(
      http.get(`${BASE}/api/audit`, () => {
        calls++;
        return HttpResponse.json(page([]));
      })
    );
    renderPage();

    await waitFor(() => expect(calls).toBe(1));

    fireEvent.click(screen.getByTitle('Reload'));

    await waitFor(() => expect(calls).toBeGreaterThanOrEqual(2));
  });

  it('takeSelector_updatesTakeQueryParam', async () => {
    let lastUrl = '';
    server.use(
      http.get(`${BASE}/api/audit`, ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(page([]));
      })
    );
    renderPage();

    await waitFor(() => expect(lastUrl).toContain('take=100'));

    const select = screen.getByDisplayValue('100');
    fireEvent.change(select, { target: { value: '500' } });

    await waitFor(() => expect(lastUrl).toContain('take=500'));
  });

  it('rendersUsernameColumn_OverShortUuidFallback', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument(),
      { timeout: 5000 });
  });

  it('rendersIpAddressColumn', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    await waitFor(() => expect(screen.getByText('10.0.0.1')).toBeInTheDocument());
  });

  it('loadMoreButton_appearsWhenNextCursorPresent', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(
      page(ENTRIES, { timestamp: '2026-04-25T11:00:00Z', id: 'e2' })
    )));
    renderPage();

    await waitFor(() => expect(screen.getByText(/Load more/)).toBeInTheDocument());
  });

  it('loadMoreButton_hiddenWhenNoCursor', async () => {
    server.use(http.get(`${BASE}/api/audit`, () => HttpResponse.json(page(ENTRIES))));
    renderPage();

    // Let the page settle first
    await waitFor(() => expect(screen.getAllByText('WORKFLOW_CREATED').length).toBeGreaterThan(0));
    expect(screen.queryByText(/Load more/)).not.toBeInTheDocument();
  });

  it('loadMorePages_persistAcrossManualRefresh', async () => {
    // Regression: setExtraPages([]) used to live inside queryFn, which also runs on the
    // 15s polling refetch and on the Refresh button. Both wiped the user's paginated
    // history. The reset must be tied to filter changes, not to the fetch event itself.
    const page1Cursor = { timestamp: '2026-04-25T10:00:00Z', id: 'cursor-1' };
    const page2 = [{
      id: 'page2-row',
      timestamp: '2026-04-25T09:00:00Z',
      userId: null, username: 'bob',
      action: 'CREDENTIAL_DECRYPTED',
      resourceType: 'Credential', resourceId: null,
      details: null, ipAddress: '10.0.0.7',
    }];
    server.use(
      http.get(`${BASE}/api/audit`, ({ request }) => {
        const url = new URL(request.url);
        // Second-page request: client sends afterTs+afterId from the first response's cursor.
        if (url.searchParams.has('afterTs')) {
          return HttpResponse.json(page(page2));
        }
        // First-page request (initial + refetches): same response — full page with a cursor.
        return HttpResponse.json(page(ENTRIES, page1Cursor));
      })
    );
    renderPage();

    await waitFor(() => expect(screen.getByText(/Load more/)).toBeInTheDocument());
    fireEvent.click(screen.getByText(/Load more/));

    // Page-2 action appears in both the row and (because the quick-filter chip lists all
    // actions present in the current view) the chip row — match on length ≥ 1.
    await waitFor(() => expect(screen.getAllByText('CREDENTIAL_DECRYPTED').length).toBeGreaterThan(0));

    fireEvent.click(screen.getByTitle('Reload'));

    // After Refresh: page 1 refetches but the loaded page 2 must still be visible.
    // The row would disappear if the reset was tied to the fetch event instead of filter
    // values, leaving the user with just the refetched page 1.
    await waitFor(() => expect(screen.getAllByText('CREDENTIAL_DECRYPTED').length).toBeGreaterThan(0),
      { timeout: 3000 });
  });

  it('typingIpAddressFilter_updatesQuery', async () => {
    let lastUrl = '';
    server.use(
      http.get(`${BASE}/api/audit`, ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(page([]));
      })
    );
    renderPage();

    fireEvent.change(screen.getByPlaceholderText('10.0.0.42'), { target: { value: '192.168.1.1' } });

    await waitFor(() => expect(lastUrl).toContain('ipAddress=192.168.1.1'));
  });
});
