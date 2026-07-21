import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { UsersPage } from '../../pages/UsersPage';
import { useAuthStore } from '../../stores/authStore';
import type { UserRow } from '../../types/api';

const BASE = 'http://localhost';
const originalMatchMedia = window.matchMedia;

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

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

const USERS: UserRow[] = [
  { id: 'u1', username: 'alice', role: 'Admin', isActive: true, createdAt: '2026-06-01T00:00:00Z' },
  {
    id: 'u2', username: 'bob', role: 'Viewer', isActive: false, createdAt: '2026-06-02T00:00:00Z',
    provider: 'ActiveDirectory', authority: 'corp.example', subject: 'S-1-5-21-1-2-3-1105',
    lastDirectorySyncAt: '2026-07-12T08:00:00Z', directorySyncStatus: 'Stale',
  },
];

function renderPage() {
  useAuthStore.setState({ isAuthenticated: true, username: 'alice', role: 'Admin', userId: 'u1' });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <UsersPage />
    </QueryClientProvider>,
  );
}

describe('UsersPage — mobile', () => {
  it('renders cards instead of a table and keeps all fields', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(USERS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());
    expect(screen.getByTestId('mobile-card-list')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getByText('bob')).toBeInTheDocument();
    expect(screen.getByText('ActiveDirectory')).toBeInTheDocument();
    expect(screen.getByText('Stale')).toBeInTheDocument();
    // Row actions present on cards (admin).
    expect(screen.getAllByTitle(/reset password/i).length).toBeGreaterThan(0);
  });
});
