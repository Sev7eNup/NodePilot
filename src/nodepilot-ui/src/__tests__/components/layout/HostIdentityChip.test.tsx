import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { TopBar } from '../../../components/layout/TopBar';
import { systemApi } from '../../../api/system';
import type { HostInfo } from '../../../api/system';
import { useAuthStore } from '../../../stores/authStore';
import { useDbHealthStore, resetDbHealth } from '../../../stores/dbHealthStore';

// The chip isn't exported on its own, so we exercise it through <TopBar />.
vi.mock('../../../api/system', () => ({
  systemApi: { getHostInfo: vi.fn() },
}));

const mockGetHostInfo = vi.mocked(systemApi.getHostInfo);

function renderTopBar() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/']}>
        <TopBar />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('TopBar host-identity chip', () => {
  beforeEach(() => {
    // BackendStatus reads the app-wide health store, whose poll lives in useDatabaseHealth
    // (mounted once in App), so drive the store directly.
    useDbHealthStore.setState({ status: 'ok' });
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    useAuthStore.setState({ isAuthenticated: true, role: 'Admin', userId: 'u1', username: 'admin' });
  });

  afterEach(() => {
    resetDbHealth();
    useAuthStore.setState({ isAuthenticated: null, role: null, userId: null, username: null });
    mockGetHostInfo.mockReset();
    vi.restoreAllMocks();
  });

  it('shows the FQDN as the host value and omits the separate FQDN/domain fields', async () => {
    mockGetHostInfo.mockResolvedValue({
      machineName: 'NPSRV01',
      fqdn: 'npsrv01.corp.example.local',
      domain: 'corp.example.local',
      appVersion: '1.2.3',
    });
    renderTopBar();

    // All values are visible at once; no hover or click is required.
    expect(await screen.findByText('npsrv01.corp.example.local')).toBeInTheDocument();
    expect(screen.getByText('Host')).toBeInTheDocument();
    expect(screen.queryByText('NPSRV01')).not.toBeInTheDocument();
    expect(screen.getByText('npsrv01.corp.example.local')).toBeInTheDocument();
    expect(screen.queryByText('corp.example.local')).not.toBeInTheDocument();
  });

  it('falls back to the machine name when no FQDN is available', async () => {
    mockGetHostInfo.mockResolvedValue({ machineName: 'KEMPISPC', fqdn: 'KempisPC', domain: null, appVersion: '1.2.3' });
    renderTopBar();

    expect(await screen.findByText('KEMPISPC')).toBeInTheDocument();
    expect(screen.queryByText('FQDN')).not.toBeInTheDocument();
  });

  it('renders nothing when the backend returns a non-object (e2e catch-all [])', async () => {
    // The hermetic e2e catch-all answers unmocked /api/* with `[]`; the chip must degrade.
    mockGetHostInfo.mockResolvedValue([] as unknown as HostInfo);
    renderTopBar();

    await waitFor(() => expect(screen.getByLabelText('API: connected')).toBeInTheDocument());
    expect(screen.queryByText(/NPSRV01/)).not.toBeInTheDocument();
  });

  it('does not query while unauthenticated', async () => {
    useAuthStore.setState({ isAuthenticated: false });
    renderTopBar();

    await waitFor(() => expect(screen.getByLabelText('API: connected')).toBeInTheDocument());
    expect(mockGetHostInfo).not.toHaveBeenCalled();
  });
});
