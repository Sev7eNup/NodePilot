import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { AlertingPage } from '../../pages/AlertingPage';
import { useAuthStore } from '../../stores/authStore';
import type { NotificationRule } from '../../api/alerting';

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

const RULES: NotificationRule[] = [
  {
    id: 'r1', name: 'Prod-Failures', description: 'alert on prod failures', isEnabled: true,
    eventTypes: ['ExecutionFailed'], filterExpressionJson: null, scopeKind: 'Global',
    cooldownMinutes: 15, minOccurrences: 1, occurrenceWindowMinutes: 0,
    routes: [{ id: 'rt1', channel: 'Email', target: 'ops@x', secret: null, order: 0 }],
    targets: [], createdAt: '2026-06-30T00:00:00Z', updatedAt: '2026-06-30T00:00:00Z', updatedBy: 'admin',
  },
];

/** Build a NotificationRule seed with sensible defaults; override what the test needs. */
const rule = (overrides: Partial<NotificationRule> = {}): NotificationRule => ({
  id: 'r0', name: 'Base', description: null, isEnabled: true,
  eventTypes: ['ExecutionFailed'], filterExpressionJson: null, scopeKind: 'Global',
  cooldownMinutes: 15, minOccurrences: 1, occurrenceWindowMinutes: 0,
  routes: [], targets: [], createdAt: '2026-06-30T00:00:00Z', updatedAt: '2026-06-30T00:00:00Z', updatedBy: 'admin',
  ...overrides,
});

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  // The page opens on the System-alerts tab (ADR 0008); these tests target the Custom-rules tab, so stub the
  // system endpoints (empty) and switch to Custom after render.
  server.use(
    http.get(`${BASE}/api/alerting/system/catalog`, () => HttpResponse.json({ sources: [] })),
    http.get(`${BASE}/api/alerting/system/policies`, () => HttpResponse.json([])),
  );
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <AlertingPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  fireEvent.click(screen.getByRole('button', { name: /Custom rules/i }));
  return result;
}

describe('AlertingPage', () => {
  it('writes the selected primary tab to the URL', () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])));
    renderPage();
    expect(screen.getByTestId('location')).toHaveTextContent('/?tab=custom');
  });

  it('emptyRules_showsEmptyMessage', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])));
    renderPage();
    await waitFor(() => expect(screen.getByText(/No alerting rules yet/i)).toBeInTheDocument());
  });

  it('rendersRuleList_withEventsAndChannels', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json(RULES)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Prod-Failures')).toBeInTheDocument());
    expect(screen.getByText(/Execution failed/i)).toBeInTheDocument();
    expect(screen.getByText(/Email/)).toBeInTheDocument();
  });

  it('rendersRuleList_withDescriptionColumn', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json(RULES)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Prod-Failures')).toBeInTheDocument());
    // Description is its own column (columnheader), not stacked under the name.
    expect(screen.getByRole('columnheader', { name: /Description/i })).toBeInTheDocument();
    expect(screen.getByText('alert on prod failures')).toBeInTheDocument();
  });

  it('adminSeesNewRuleButton', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No alerting rules yet/i)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /New rule/i })).toBeInTheDocument();
  });

  it('viewerRole_hidesNewRuleButton', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])));
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText(/No alerting rules yet/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New rule/i })).not.toBeInTheDocument();
  });

  it('clickNewRule_opensEditorWithCreateTitle', async () => {
    server.use(
      http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/shared-workflow-folders`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New rule/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New rule/i }));
    expect(await screen.findByText(/Create alerting rule/i)).toBeInTheDocument();
  });

  it('opensDeliveriesModal_andRendersLedger', async () => {
    server.use(
      http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/alerting/deliveries`, () => HttpResponse.json([
        {
          id: 'd1', ruleId: 'r1', ruleName: 'Prod-Fail', routeId: 'rt1', channel: 'Email', target: 'ops@x',
          eventKey: 'e1', status: 'Failed', attempt: 2, createdAt: '2026-06-30T00:00:00Z',
          sentAt: '2026-06-30T00:00:00Z', error: 'smtp down', isTest: false, summary: null,
        },
      ])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Deliveries/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Deliveries/i }));
    expect(await screen.findByText('Prod-Fail')).toBeInTheDocument();
    expect(screen.getByText('smtp down')).toBeInTheDocument();
  });

  it('searchFilters_byName', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json(RULES)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Prod-Failures')).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText(/Search by name/i), { target: { value: 'nomatch' } });
    expect(screen.queryByText('Prod-Failures')).not.toBeInTheDocument();
    expect(screen.getByText(/No rule matches/i)).toBeInTheDocument();
  });

  // Column-header click-to-sort (mirrors CustomActivitiesPage / MaintenanceWindowsPage).
  const rowNames = (container: HTMLElement) =>
    Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-semibold')?.textContent?.trim());

  it('defaultSort_isNameAsc', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([
      rule({ id: 'a', name: 'Charlie' }),
      rule({ id: 'b', name: 'Alpha' }),
      rule({ id: 'c', name: 'Bravo' }),
    ])));
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Bravo')).toBeInTheDocument());
    expect(rowNames(container)).toEqual(['Alpha', 'Bravo', 'Charlie']); // default name asc
  });

  it('clickNameSort_togglesAscDesc', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([
      rule({ id: 'a', name: 'Charlie' }),
      rule({ id: 'b', name: 'Alpha' }),
      rule({ id: 'c', name: 'Bravo' }),
    ])));
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Bravo')).toBeInTheDocument());
    expect(rowNames(container)).toEqual(['Alpha', 'Bravo', 'Charlie']); // asc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(rowNames(container)).toEqual(['Charlie', 'Bravo', 'Alpha']); // desc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(rowNames(container)).toEqual(['Alpha', 'Bravo', 'Charlie']); // back to asc
  });

  it('sortByEnabled_activeFirst', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([
      rule({ id: 'a', name: 'Disabled One', isEnabled: false }),
      rule({ id: 'b', name: 'Enabled One', isEnabled: true }),
    ])));
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Enabled One')).toBeInTheDocument());
    expect(rowNames(container)).toEqual(['Disabled One', 'Enabled One']); // default name asc
    fireEvent.click(screen.getByRole('button', { name: /^Enabled$/ }));
    expect(rowNames(container)).toEqual(['Enabled One', 'Disabled One']); // active first on asc
    fireEvent.click(screen.getByRole('button', { name: /^Enabled$/ }));
    expect(rowNames(container)).toEqual(['Disabled One', 'Enabled One']); // desc
  });

  it('sortByDescription_alphabetical', async () => {
    server.use(http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json([
      rule({ id: 'a', name: 'N1', description: 'ccc' }),
      rule({ id: 'b', name: 'N2', description: 'aaa' }),
      rule({ id: 'c', name: 'N3', description: 'bbb' }),
    ])));
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('N2')).toBeInTheDocument());
    expect(rowNames(container)).toEqual(['N1', 'N2', 'N3']); // default name asc (descriptions ccc,aaa,bbb)
    fireEvent.click(screen.getByRole('button', { name: /^Description$/ }));
    // asc by description: aaa(N2), bbb(N3), ccc(N1)
    expect(rowNames(container)).toEqual(['N2', 'N3', 'N1']);
  });
});
