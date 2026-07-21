import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { SystemInfoSection } from '../../../components/admin-settings/SystemInfoSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SystemInfoSection />
    </QueryClientProvider>,
  );
}

describe('SystemInfoSection', () => {
  it('renders the bootstrap-only metadata cards', async () => {
    server.use(http.get('/api/admin/settings/system-info', () => HttpResponse.json({
      appVersion: '1.2.3.4',
      overridesPath: 'C:\\ProgramData\\NodePilot\\appsettings.runtime.json',
      databaseProvider: 'postgres',
      databaseHost: 'db.internal',
      secretsProvider: 'AesGcm',
      clusterEnabled: true,
      clusterNodeId: 'np-01',
      clusterIsLeader: true,
      jwtIssuer: 'NodePilot-Prod',
      jwtAudience: 'NodePilot-Prod',
    })));

    renderSection();
    await waitFor(() => expect(screen.getByText('1.2.3.4')).toBeInTheDocument());
    expect(screen.getByText('db.internal')).toBeInTheDocument();
    expect(screen.getByText('AesGcm')).toBeInTheDocument();
    expect(screen.getByText('np-01')).toBeInTheDocument();
    expect(screen.getAllByText('true').length).toBeGreaterThan(0);
  });

  it('renders an error state when the system-info endpoint fails', async () => {
    server.use(http.get('/api/admin/settings/system-info', () =>
      HttpResponse.json({ code: 'BANG', message: 'boom' }, { status: 500 }),
    ));
    renderSection();
    await waitFor(() => expect(screen.getByText(/500|boom|failed/i)).toBeInTheDocument());
  });
});
