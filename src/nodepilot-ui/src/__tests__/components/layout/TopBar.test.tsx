import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { TopBar } from '../../../components/layout/TopBar';
import { useAuthStore } from '../../../stores/authStore';
import { useDbHealthStore, resetDbHealth } from '../../../stores/dbHealthStore';

function renderAt(path: string) {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role: 'Admin' });
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <TopBar />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
  resetDbHealth();
});

describe('TopBar', () => {
  it('shows the section title for the current route', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderAt('/workflows');
    expect(screen.getByText('Workflows')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Workspace' })).toHaveAttribute('href', '/');
    expect(screen.getByText('Workflows')).toHaveAttribute('aria-current', 'page');
  });

  it('renders nested settings breadcrumbs from the URL', () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderAt('/settings?tab=system&section=security');
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Administration' })).toHaveAttribute('href', '/users');
    expect(screen.getByRole('link', { name: 'Settings' })).toHaveAttribute('href', '/settings?tab=personal');
    expect(screen.getByRole('link', { name: 'System' })).toHaveAttribute('href', '/settings?tab=system&section=integrations');
    expect(screen.getByText('Security')).toHaveAttribute('aria-current', 'page');
  });

  it('resolves the title for a sub-route via prefix match', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderAt('/global-variables');
    expect(screen.getByText('Globals')).toBeInTheDocument();
  });

  // The BackendStatus pill shows a compact "API" label + a colour-coded plug icon; the
  // connection state lives in the accessible name (aria-label "API: <state>"), not as visible
  // text — so assert via the accessible label. The pill no longer runs its own poll: it renders
  // whatever the app-wide database-health probe (useDatabaseHealth, mounted once in App) wrote
  // into the store — so the tests drive the store, which is the pill's actual input.
  it('reports the backend as connected when the health probe says ok', async () => {
    useDbHealthStore.setState({ status: 'ok' });
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*connected/i)).toBeInTheDocument());
  });

  it('reports the backend as unreachable when the probe request itself fails', async () => {
    useDbHealthStore.setState({ status: 'offline' });
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*unreachable/i)).toBeInTheDocument());
  });

  it('reports the database as unreachable distinctly from the process being down', async () => {
    // The old pill probed /healthz/live, which stays 200 through a database outage — it showed
    // green while every data query failed. This state is the fix for that misleading indicator.
    useDbHealthStore.setState({ status: 'unavailable', reason: 'Unreachable' });
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*database unreachable/i)).toBeInTheDocument());
  });

  it('renders the armed state as connected — one slow query is not an outage', async () => {
    useDbHealthStore.setState({ status: 'armed' });
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*connected/i)).toBeInTheDocument());
  });
});
