import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { MaintenanceWindowsPage } from '../../pages/MaintenanceWindowsPage';
import { useAuthStore } from '../../stores/authStore';

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

const WINDOWS = [{
  id: 'w1', name: 'Weekend Blackout', description: 'No prod runs', isEnabled: true,
  mode: 'Blackout', scopeKind: 'Global', recurrence: 'Weekly',
  oneTimeStartUtc: null, oneTimeEndUtc: null, weeklyDaysMask: 65,
  weeklyStartMinuteOfDay: 1320, weeklyEndMinuteOfDay: 120, cronExpression: null,
  durationMinutes: null, timeZoneId: 'UTC', targets: [],
  createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin',
}];

function renderPage() {
  useAuthStore.setState({ isAuthenticated: true, username: 'admin', role: 'Admin', userId: 'u1' });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MaintenanceWindowsPage />
    </QueryClientProvider>,
  );
}

describe('MaintenanceWindowsPage — mobile', () => {
  it('renders cards instead of a table at the mobile breakpoint', async () => {
    server.use(http.get(`${BASE}/api/maintenance-windows`, () => HttpResponse.json(WINDOWS)));
    server.use(http.get(`${BASE}/api/shared-folders`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Weekend Blackout')).toBeInTheDocument());
    expect(screen.getByTestId('mobile-card-list')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getByTitle('Edit')).toBeInTheDocument();
  });
});
