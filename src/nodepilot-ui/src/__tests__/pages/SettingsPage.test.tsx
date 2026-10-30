import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { SettingsPage } from '../../pages/SettingsPage';
import { useAuthStore } from '../../stores/authStore';

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

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin', path = '/settings') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <SettingsPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('SettingsPage', () => {
  it('writes top-level tab changes to the URL', () => {
    const { container } = renderPage('Admin');
    const systemTab = [...container.querySelectorAll<HTMLButtonElement>('.np-tab-list button')]
      .find((button) => button.textContent?.trim() === 'System');
    fireEvent.click(systemTab!);
    expect(screen.getByTestId('location')).toHaveTextContent('/settings?tab=system&section=integrations');
  });

  it('falls back to personal for a non-admin system deep link', () => {
    const { container } = renderPage('Viewer', '/settings?tab=system&section=security');
    expect(screen.getByRole('heading', { name: /appearance/i })).toBeInTheDocument();
    expect(container.querySelector('.np-tab-list')).not.toBeInTheDocument();
  });

  it.each(['Admin', 'Operator', 'Viewer'] as const)(
    'personalTab_rendersNoCredentialsAndRequestsNone (%s)',
    async (role) => {
      // Credentials have their own page; a request here would 403 for a Viewer.
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
      renderPage(role);
      await act(async () => {});

      expect(screen.getByRole('heading', { name: /appearance/i })).toBeInTheDocument();
      expect(screen.queryByRole('heading', { name: /credentials/i })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /Add Credential/i })).not.toBeInTheDocument();
      const requested = vi.mocked(globalThis.fetch).mock.calls
        .some(([input]) => String(input).includes('/credentials'));
      expect(requested).toBe(false);
    },
  );
});
