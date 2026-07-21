import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { TopBar } from '../../../components/layout/TopBar';
import { useAuthStore } from '../../../stores/authStore';

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

afterEach(() => vi.restoreAllMocks());

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
  // text — so assert via the accessible label.
  it('reports the backend as connected when /healthz/live is ok', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*connected/i)).toBeInTheDocument());
    expect(fetchSpy).toHaveBeenCalledWith('/healthz/live', expect.objectContaining({ cache: 'no-store' }));
  });

  it('reports the backend as unreachable on a network error', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('network down'));
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*unreachable/i)).toBeInTheDocument());
  });

  it('reports the backend as unreachable on a non-2xx response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 503 }));
    renderAt('/');
    await waitFor(() => expect(screen.getByLabelText(/API:\s*unreachable/i)).toBeInTheDocument());
  });
});
