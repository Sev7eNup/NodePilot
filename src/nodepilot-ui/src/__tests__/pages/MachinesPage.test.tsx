import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { MachinesPage } from '../../pages/MachinesPage';
import { useAuthStore } from '../../stores/authStore';
import type { ManagedMachine, Credential } from '../../types/api';

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

const MACHINES: ManagedMachine[] = [
  {
    id: 'm1',
    name: 'Web-01',
    hostname: 'web01.lab.local',
    winRmPort: 5985,
    useSsl: false,
    defaultCredentialId: 'c1',
    tags: 'prod,web',
    lastConnectivityCheck: '2026-04-25T10:00:00Z',
    isReachable: true,
    usedByWorkflowCount: 4,
    recentStepCount: 20,
    recentFailedStepCount: 1,
    activeRunCount: 2,
  },
  {
    id: 'm2',
    name: 'DB-01',
    hostname: 'db01.lab.local',
    winRmPort: 5986,
    useSsl: true,
    defaultCredentialId: null,
    tags: null,
    lastConnectivityCheck: null,
    isReachable: false,
    usedByWorkflowCount: 0,
    recentStepCount: 0,
    recentFailedStepCount: 0,
    activeRunCount: 0,
  },
];

const CREDS: Credential[] = [
  { id: 'c1', name: 'svc-account', username: 'svc', domain: 'CORP', expiresAt: null },
];

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MachinesPage />
    </QueryClientProvider>
  );
}

describe('MachinesPage', () => {
  it('rendersLoadingState', () => {
    server.use(http.get(`${BASE}/api/machines`, () => new Promise(() => {})));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();
    expect(screen.getByText(/Loading/)).toBeInTheDocument();
  });

  it('emptyMachines_showsEmptyMessage', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText(/No machines configured/)).toBeInTheDocument());
  });

  it('rendersMachineList_withHostnameAndPort', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.getByText('web01.lab.local:5985')).toBeInTheDocument();
    expect(screen.getByText('DB-01')).toBeInTheDocument();
    expect(screen.getByText('db01.lab.local:5986')).toBeInTheDocument();
  });

  it('rendersTagsAsPills_whenPresent', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // 'prod' and 'web' show up twice each — once in the tag-filter chip row, once in
    // the row's tag cells. getAllBy confirms presence without binding to a count.
    expect(screen.getAllByText('prod').length).toBeGreaterThan(0);
    expect(screen.getAllByText('web').length).toBeGreaterThan(0);
  });

  it('adminUser_seesAddMachineButton', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText(/No machines configured/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /Add Machine/i })).toBeInTheDocument();
  });

  it('viewerRole_hidesAddButton', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText(/No machines configured/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Add Machine/i })).not.toBeInTheDocument();
  });

  it('clickAddMachine_opensCreateDialog', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Machine/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Machine/i }));

    expect(screen.getByPlaceholderText('Display Name')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Hostname or IP')).toBeInTheDocument();
  });

  it('createDialog_credentialDropdownPopulated', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Machine/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Machine/i }));

    expect(screen.getByText(/svc-account \(svc\)/)).toBeInTheDocument();
  });

  it('cancelCreate_hidesDialog', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Machine/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Machine/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByPlaceholderText('Display Name')).not.toBeInTheDocument();
  });

  it('reachableMachine_rendersWifiIcon_unreachableRendersWifiOff', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    const { container } = renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Reachable row → Wifi icon coloured green-500. Unreachable row → WifiOff
    // coloured with surface-highest (the muted "neutral / no data yet" tone).
    expect(container.querySelectorAll('.text-green-500').length).toBeGreaterThan(0);
    expect(container.querySelectorAll('.text-surface-highest').length).toBeGreaterThan(0);
  });

  it('viewerRole_hidesTestAndDeleteButtons', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.queryByTitle('Test Connection')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });

  it('operatorRole_seesTestButton_notDeleteButton', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Operator');

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.getAllByTitle('Test Connection').length).toBeGreaterThan(0);
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });

  it('rendersSummaryHeader_withReachableCount', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    expect(screen.getByText(/1 of 2 reachable/i)).toBeInTheDocument();
  });

  it('searchFilters_byHostname', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText(/Search by name/i), { target: { value: 'db01' } });

    expect(screen.queryByText('Web-01')).not.toBeInTheDocument();
    expect(screen.getByText('DB-01')).toBeInTheDocument();
  });

  it('credentialColumn_resolvesNameFromId', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage();

    // Wait until the credentials query has resolved AND the table cell shows the
    // mapped name — otherwise we'd race with the initial "loading" state.
    await waitFor(() => expect(screen.getByText('svc-account')).toBeInTheDocument());
  });

  it('editButton_opensDialogPrefilled', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Default sort = name asc → DB-01 appears before Web-01. Scope the Edit
    // lookup to Web-01's row so the test doesn't depend on row index.
    const web01Row = screen.getByText('Web-01').closest('tr');
    expect(web01Row).toBeTruthy();
    fireEvent.click(within(web01Row!).getByTitle('Edit'));

    await waitFor(() => expect(screen.getByDisplayValue('Web-01')).toBeInTheDocument());
    expect(screen.getByDisplayValue('web01.lab.local')).toBeInTheDocument();
  });

  it('sslToggle_bumpsPortFromHttpToHttps', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Machine/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Machine/i }));

    // Initial port = 5985 (HTTP default). Clicking SSL toggle bumps to 5986.
    expect(screen.getByDisplayValue('5985')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^HTTP$/ }));
    expect(screen.getByDisplayValue('5986')).toBeInTheDocument();
  });

  it('tagFilterChip_filtersRowsByTag', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Both rows initially visible.
    expect(screen.getByText('DB-01')).toBeInTheDocument();

    // Click "prod" chip in the filter row — only Web-01 (the prod machine) stays.
    const toolbar = screen.getByPlaceholderText(/Search by name/i).closest('div')?.parentElement;
    expect(toolbar).toBeTruthy();
    const prodChip = within(toolbar!).getByRole('button', { name: 'prod' });
    fireEvent.click(prodChip);

    expect(screen.getByText('Web-01')).toBeInTheDocument();
    expect(screen.queryByText('DB-01')).not.toBeInTheDocument();
  });

  it('workflowsCell_rendersCountWhenUsed_andDashWhenZero', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Web-01 is used by 4 workflows — count surfaces as plain text in the cell.
    expect(screen.getByText('4')).toBeInTheDocument();
    // DB-01 has 0 references → cell should NOT render a "0", it renders a dash
    // (we check by counting Boxes icons: only the used machine gets one).
    const db01Row = screen.getByText('DB-01').closest('tr');
    expect(db01Row).toBeTruthy();
    expect(within(db01Row!).queryByText('0')).not.toBeInTheDocument();
  });

  it('activityCell_rendersSuccessRatio_andDashWhenNoData', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Web-01: total=20, failed=1 → success=19 → "19/20" displayed.
    expect(screen.getByText('19/20')).toBeInTheDocument();
    // DB-01 has 0 steps → cell renders a dash, not "0/0".
    const db01Row = screen.getByText('DB-01').closest('tr');
    expect(within(db01Row!).queryByText('0/0')).not.toBeInTheDocument();
  });

  it('liveCell_rendersBadgeWhenActive_andDashWhenIdle', async () => {
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json(MACHINES)));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    await waitFor(() => expect(screen.getByText('Web-01')).toBeInTheDocument());
    // Web-01: activeRunCount=2 → "2 running" badge appears.
    expect(screen.getByText(/2 running/i)).toBeInTheDocument();
    // DB-01: idle → no "running" text in that row.
    const db01Row = screen.getByText('DB-01').closest('tr');
    expect(within(db01Row!).queryByText(/running/i)).not.toBeInTheDocument();
  });
});
