import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { MachinesPage } from '../../pages/MachinesPage';
import { useAuthStore } from '../../stores/authStore';
import type { ManagedMachine, Credential } from '../../types/api';

const BASE = 'http://localhost';
const originalMatchMedia = window.matchMedia;

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

/** Force the mobile breakpoint so MachinesPage renders cards instead of the table. */
function setMobile() {
  window.matchMedia = ((q: string) => ({
    matches: true, media: q, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => setMobile());
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); window.matchMedia = originalMatchMedia; });
afterAll(() => server.close());

const MACHINES: ManagedMachine[] = [
  {
    id: 'm1', name: 'Web-01', hostname: 'web01.lab.local', winRmPort: 5985, useSsl: false,
    defaultCredentialId: 'c1', tags: 'prod,web', lastConnectivityCheck: '2026-04-25T10:00:00Z',
    isReachable: true, usedByWorkflowCount: 4, recentStepCount: 20, recentFailedStepCount: 1, activeRunCount: 2,
  },
];
const CREDS: Credential[] = [{ id: 'c1', name: 'svc-account', username: 'svc', domain: 'CORP', expiresAt: null }];

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MachinesPage />
    </QueryClientProvider>,
  );
}

describe('MachinesPage — mobile', () => {
  it('renders cards instead of a table at the mobile breakpoint', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.getByTestId('mobile-card-list')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    // Full data still present — value + a label/value field.
    expect(screen.getByText('web01.lab.local:5985')).toBeInTheDocument();
    expect(screen.getByText('svc-account')).toBeInTheDocument();
  });

  it('keeps row actions wired on cards for an admin', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.getByTitle('Edit')).toBeInTheDocument();
    expect(screen.getByTitle('Delete')).toBeInTheDocument();
  });

  it('hides write actions on cards for a viewer', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.queryByTitle('Edit')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });
});
