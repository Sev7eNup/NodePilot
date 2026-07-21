import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { SystemAlertsSection } from '../../components/alerting/SystemAlertsSection';
import { useAuthStore } from '../../stores/authStore';
import type { SystemAlertCatalog, SystemAlertPolicy } from '../../api/systemAlerting';

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const CATALOG: SystemAlertCatalog = {
  sources: [
    {
      sourceId: 'backlog', category: 'Queue', scopeCapability: 'GlobalOnly', defaultSeverity: 'Warning',
      fields: [{ name: 'depth', type: 'Number', operators: ['>', '<'], unit: 'count', enumValues: null }],
      parameters: [], presets: [{ presetId: 'high', severity: 'Warning', sustainForSeconds: 60, conditionJson: null, parameters: null }],
      available: true,
    },
    {
      sourceId: 'machine-unreachable', category: 'Health', scopeCapability: 'GlobalOnly', defaultSeverity: 'Critical',
      fields: [{ name: 'reachable', type: 'Boolean', operators: ['isTrue', 'isFalse'], unit: null, enumValues: null }],
      parameters: [], presets: [], available: false,
    },
  ],
};

const POLICIES: SystemAlertPolicy[] = [
  {
    id: 'p1', name: 'Backlog critical', description: null, isEnabled: true, sourceId: 'backlog', presetId: 'high',
    sourceParameters: null, conditionJson: null, sustainForSeconds: 60, severityOverride: 'Critical', scopeKind: 'Global',
    targets: [], routes: [{ id: 'rt1', channel: 'Email', target: 'ops@x', secret: null, order: 0 }],
    cooldownMinutes: 0, minOccurrences: 1, occurrenceWindowMinutes: 0,
    createdAt: '2026-07-10T00:00:00Z', updatedAt: '2026-07-10T00:00:00Z', updatedBy: 'admin', activatedAt: '2026-07-10T00:00:00Z',
  },
];

function renderSection(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin', policies: SystemAlertPolicy[] = POLICIES) {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  server.use(
    http.get(`${BASE}/api/alerting/system/catalog`, () => HttpResponse.json(CATALOG)),
    http.get(`${BASE}/api/alerting/system/policies`, () => HttpResponse.json(policies)),
  );
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><SystemAlertsSection /></QueryClientProvider>);
}

describe('SystemAlertsSection', () => {
  it('rendersCatalogCards_groupedByCategory', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByText('Execution backlog')).toBeInTheDocument());
    expect(screen.getByText('Machine unreachable')).toBeInTheDocument();
    // Category headers
    expect(screen.getByText(/^Queue$/i)).toBeInTheDocument();
    expect(screen.getByText(/^Health$/i)).toBeInTheDocument();
  });

  it('showsActiveStatus_forSourceWithEnabledPolicy', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByText('Backlog critical')).toBeInTheDocument());
    expect(screen.getByText(/^Active$/i)).toBeInTheDocument();
  });

  it('showsUnavailableStatus_forUnavailableSource', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByText('Machine unreachable')).toBeInTheDocument());
    expect(screen.getByText(/^Unavailable$/i)).toBeInTheDocument();
  });

  it('showsNotConfigured_whenSourceHasNoPolicies', async () => {
    renderSection('Admin', []);
    await waitFor(() => expect(screen.getByText('Execution backlog')).toBeInTheDocument());
    expect(screen.getAllByText(/Not configured/i).length).toBeGreaterThan(0);
  });

  it('adminSeesAddPolicy_viewerDoesNot', async () => {
    renderSection('Admin', []);
    await waitFor(() => expect(screen.getByText('Execution backlog')).toBeInTheDocument());
    expect(screen.getAllByText(/Add policy/i).length).toBeGreaterThan(0);

    server.resetHandlers();
    vi.restoreAllMocks();
    renderSection('Viewer', []);
    await waitFor(() => expect(screen.getAllByText('Execution backlog').length).toBeGreaterThan(0));
    expect(screen.queryByText(/Add policy/i)).not.toBeInTheDocument();
  });
});
