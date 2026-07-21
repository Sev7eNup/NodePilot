import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { MaintenanceWindowsPage } from '../../pages/MaintenanceWindowsPage';
import { useAuthStore } from '../../stores/authStore';
import { confirmDialog } from '../../stores/confirmStore';

vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});

const BASE = 'http://localhost';

// authedFetch prefixes every call with the relative '/api'. jsdom needs an absolute URL,
// so rewrite leading-'/' paths onto BASE — same shim MachinesPage.test uses.
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
beforeEach(() => { vi.mocked(confirmDialog).mockClear().mockResolvedValue(true); });
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

type MwOverrides = Partial<Record<string, unknown>>;
const mw = (overrides: MwOverrides = {}) => ({
  id: 'mw-1',
  name: 'Nightly Patch',
  description: null,
  isEnabled: true,
  mode: 'Blackout',
  scopeKind: 'Global',
  recurrence: 'Weekly',
  oneTimeStartUtc: null,
  oneTimeEndUtc: null,
  weeklyDaysMask: 0b0111110, // Mon-Fri
  weeklyStartMinuteOfDay: 22 * 60,
  weeklyEndMinuteOfDay: 2 * 60,
  cronExpression: null,
  durationMinutes: null,
  timeZoneId: 'UTC',
  targets: [] as Array<{ targetKind: string; targetId: string }>,
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
  updatedBy: 'admin',
  ...overrides,
});

/** Register the three GET queries the page issues. Pass `null` for windows to keep it loading. */
function seed(opts: {
  windows?: ReturnType<typeof mw>[] | null;
  folders?: Array<{ id: string; path: string }>;
  workflows?: Array<{ id: string; name: string }>;
} = {}) {
  const { windows = [], folders = [], workflows = [] } = opts;
  server.use(
    http.get(`${BASE}/api/maintenance-windows`, () =>
      windows === null ? new Promise<Response>(() => {}) : HttpResponse.json(windows)),
    http.get(`${BASE}/api/shared-workflow-folders`, () => HttpResponse.json(folders)),
    http.get(`${BASE}/api/workflows`, () => HttpResponse.json(workflows)),
  );
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MaintenanceWindowsPage />
    </QueryClientProvider>,
  );
}

// The dialog has role="presentation" (not "dialog") and unlabeled inputs, so we scope to the
// heading's parent panel and pick fields by role/order — mirrors the e2e spec's approach.
const getDialog = () =>
  screen.getByRole('heading', { name: /create maintenance window|edit maintenance window/i })
    .parentElement as HTMLElement;

describe('MaintenanceWindowsPage', () => {
  it('rendersLoadingState', () => {
    seed({ windows: null });
    renderPage();
    expect(screen.getByText(/Loading/)).toBeInTheDocument();
  });

  it('emptyWindows_showsEmptyMessage', async () => {
    seed({ windows: [] });
    renderPage();
    await waitFor(() => expect(screen.getByText(/No maintenance windows yet/i)).toBeInTheDocument());
  });

  it('rendersList_withModeScopeAndResolvedTargetNames', async () => {
    seed({
      windows: [
        mw({ id: 'g', name: 'Global Freeze', mode: 'Blackout', scopeKind: 'Global' }),
        mw({
          id: 'w', name: 'Deploy AllowOnly', mode: 'AllowOnly', scopeKind: 'Workflows',
          targets: [{ targetKind: 'Workflow', targetId: 'wf-1' }],
        }),
      ],
      workflows: [{ id: 'wf-1', name: 'Deploy Prod' }],
    });
    renderPage();

    await waitFor(() => expect(screen.getByText('Global Freeze')).toBeInTheDocument());
    expect(screen.getByText('Deploy AllowOnly')).toBeInTheDocument();
    expect(screen.getByText(/Blackout/)).toBeInTheDocument();
    expect(screen.getByText(/Allow only/i)).toBeInTheDocument();
    // The Workflows-scoped row resolves its target id to the workflow name.
    expect(screen.getByText('Deploy Prod')).toBeInTheDocument();
  });

  it('danglingTarget_rendersDeletedPlaceholder', async () => {
    seed({
      windows: [mw({
        id: 'w', name: 'Orphan', scopeKind: 'Workflows',
        targets: [{ targetKind: 'Workflow', targetId: 'gone' }],
      })],
      workflows: [], // the referenced workflow no longer exists
    });
    renderPage();

    await waitFor(() => expect(screen.getByText('Orphan')).toBeInTheDocument());
    expect(screen.getByText(/\(deleted\)/i)).toBeInTheDocument();
  });

  it('adminUser_seesNewWindowButton', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No maintenance windows yet/i)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument();
  });

  it('viewerRole_hidesNewAndRowActions', async () => {
    seed({ windows: [mw({ name: 'RO' })] });
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText('RO')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New Window/i })).not.toBeInTheDocument();
    expect(screen.queryByTitle('Edit')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });

  it('clickNewWindow_opensCreateDialog', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));
    expect(screen.getByRole('heading', { name: /Create Maintenance Window/i })).toBeInTheDocument();
  });

  it('createSubmit_disabledUntilNameAndDaySelected', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    const dialog = getDialog();
    const submit = () => within(dialog).getByRole('button', { name: 'Create' });
    expect(submit()).toBeDisabled(); // no name, no weekday

    fireEvent.change(within(dialog).getAllByRole('textbox')[0], { target: { value: 'Patch Window' } });
    expect(submit()).toBeDisabled(); // name set, but Weekly still needs a day

    fireEvent.click(within(dialog).getByRole('button', { name: 'Mon' }));
    expect(submit()).toBeEnabled();
  });

  it('createSubmit_postsTranslatedBody', async () => {
    let posted: Record<string, unknown> | null = null;
    seed({ windows: [] });
    server.use(http.post(`${BASE}/api/maintenance-windows`, async ({ request }) => {
      posted = (await request.json()) as Record<string, unknown>;
      return HttpResponse.json(mw({ id: 'created' }), { status: 201 });
    }));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    const dialog = getDialog();
    fireEvent.change(within(dialog).getAllByRole('textbox')[0], { target: { value: 'Nightly Vitest' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Mon' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(posted).not.toBeNull());
    // Mon == bit 1 == mask 2; default 22:00 -> 1320, 02:00 -> 120 (wrap past midnight).
    expect(posted).toMatchObject({
      name: 'Nightly Vitest',
      mode: 'Blackout',
      scopeKind: 'Global',
      recurrence: 'Weekly',
      weeklyDaysMask: 2,
      weeklyStartMinuteOfDay: 1320,
      weeklyEndMinuteOfDay: 120,
    });
    // Dialog closes on success.
    await waitFor(() =>
      expect(screen.queryByRole('heading', { name: /Create Maintenance Window/i })).not.toBeInTheDocument());
  });

  it('cancelCreate_hidesDialog', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    fireEvent.click(within(getDialog()).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('heading', { name: /Create Maintenance Window/i })).not.toBeInTheDocument();
  });

  it('editButton_opensDialogPrefilled', async () => {
    seed({ windows: [mw({ id: 'e1', name: 'Edit Me' })] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Edit Me')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Edit'));
    expect(screen.getByRole('heading', { name: /Edit Maintenance Window/i })).toBeInTheDocument();
    expect(screen.getByDisplayValue('Edit Me')).toBeInTheDocument();
  });

  it('deleteButton_confirmsThenCallsDelete', async () => {
    let deleted = false;
    seed({ windows: [mw({ id: 'd1', name: 'Doomed' })] });
    server.use(http.delete(`${BASE}/api/maintenance-windows/d1`, () => {
      deleted = true;
      return new HttpResponse(null, { status: 204 });
    }));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Doomed')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Delete'));
    expect(confirmDialog).toHaveBeenCalledWith(
      expect.objectContaining({ message: expect.stringContaining('Doomed'), danger: true }),
    );
    await waitFor(() => expect(deleted).toBe(true));
  });

  it('deleteButton_whenConfirmCancelled_doesNotCallDelete', async () => {
    let deleted = false;
    seed({ windows: [mw({ id: 'd2', name: 'Spared' })] });
    server.use(http.delete(`${BASE}/api/maintenance-windows/d2`, () => {
      deleted = true;
      return new HttpResponse(null, { status: 204 });
    }));
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Spared')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Delete'));
    // Give any (erroneous) request a tick to fire, then assert it never did.
    await new Promise((r) => setTimeout(r, 50));
    expect(deleted).toBe(false);
  });

  it('searchFilters_byName', async () => {
    seed({ windows: [mw({ id: 'a', name: 'Alpha Window' }), mw({ id: 'b', name: 'Bravo Window' })] });
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha Window')).toBeInTheDocument());

    fireEvent.change(screen.getByPlaceholderText(/Search by name/i), { target: { value: 'bravo' } });
    expect(screen.queryByText('Alpha Window')).not.toBeInTheDocument();
    expect(screen.getByText('Bravo Window')).toBeInTheDocument();
  });

  it('scopeWorkflows_revealsWorkflowChecklist', async () => {
    seed({ windows: [], workflows: [{ id: 'wf-1', name: 'Pick Me Workflow' }] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    const dialog = getDialog();
    // Scope is the 3rd <select> (after Mode + Recurrence). Switch to Workflows.
    const scopeSelect = within(dialog).getAllByRole('combobox')[2];
    fireEvent.change(scopeSelect, { target: { value: 'Workflows' } });

    expect(within(dialog).getByText('Pick Me Workflow')).toBeInTheDocument();
  });

  it('recurrenceSelect_offersCronOption', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    const recurrence = within(getDialog()).getAllByRole('combobox')[1];
    expect(within(recurrence).getByRole('option', { name: 'Cron' })).toBeInTheDocument();
  });

  it('editCronWindow_keepsCronRecurrenceAndPrefillsFields', async () => {
    // Regression: the editor used to coerce Cron -> Weekly and drop the schedule on edit.
    seed({
      windows: [mw({
        id: 'c1', name: 'Cron Win', recurrence: 'Cron',
        cronExpression: '0 0 3 ? * SAT', durationMinutes: 45,
        weeklyDaysMask: 0, weeklyStartMinuteOfDay: null, weeklyEndMinuteOfDay: null,
      })],
    });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Cron Win')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Edit'));
    const dialog = getDialog();
    const recurrence = within(dialog).getAllByRole('combobox')[1] as HTMLSelectElement;
    expect(recurrence.value).toBe('Cron');
    expect(within(dialog).getByDisplayValue('0 0 3 ? * SAT')).toBeInTheDocument();
    expect(within(dialog).getByDisplayValue('45')).toBeInTheDocument();
  });

  it('createSubmit_cronRecurrence_postsCronFields', async () => {
    let posted: Record<string, unknown> | null = null;
    seed({ windows: [] });
    server.use(http.post(`${BASE}/api/maintenance-windows`, async ({ request }) => {
      posted = (await request.json()) as Record<string, unknown>;
      return HttpResponse.json(mw({ id: 'created-cron' }), { status: 201 });
    }));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    const dialog = getDialog();
    fireEvent.change(within(dialog).getAllByRole('combobox')[1], { target: { value: 'Cron' } });
    fireEvent.change(within(dialog).getAllByRole('textbox')[0], { target: { value: 'Cron Vitest' } });
    // Textboxes in the Cron dialog: [0] name, [1] description, [2] cron expression, [3] timezone.
    fireEvent.change(within(dialog).getAllByRole('textbox')[2], { target: { value: '0 0 3 ? * SAT' } });
    fireEvent.change(within(dialog).getByRole('spinbutton'), { target: { value: '45' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted).toMatchObject({
      name: 'Cron Vitest',
      recurrence: 'Cron',
      cronExpression: '0 0 3 ? * SAT',
      durationMinutes: 45,
      weeklyDaysMask: 0,
      weeklyStartMinuteOfDay: null,
      weeklyEndMinuteOfDay: null,
    });
  });

  it('recurrenceOneTime_swapsWeekdayButtonsForDatetimeInputs', async () => {
    seed({ windows: [] });
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Window/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Window/i }));

    let dialog = getDialog();
    expect(within(dialog).getByRole('button', { name: 'Mon' })).toBeInTheDocument();

    // Recurrence is the 2nd <select> (after Mode). Switch to One time.
    fireEvent.change(within(dialog).getAllByRole('combobox')[1], { target: { value: 'OneTime' } });

    dialog = getDialog();
    expect(within(dialog).queryByRole('button', { name: 'Mon' })).not.toBeInTheDocument();
    expect(dialog.querySelectorAll('input[type="datetime-local"]')).toHaveLength(2);
  });

  it('clickNameSort_togglesAscDesc', async () => {
    seed({
      windows: [
        mw({ id: 'a', name: 'Charlie' }),
        mw({ id: 'b', name: 'Alpha' }),
        mw({ id: 'c', name: 'Bravo' }),
      ],
    });
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Bravo')).toBeInTheDocument());
    const names = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-semibold')?.textContent?.trim());
    expect(names()).toEqual(['Alpha', 'Bravo', 'Charlie']); // default asc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(names()).toEqual(['Charlie', 'Bravo', 'Alpha']); // toggled to desc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(names()).toEqual(['Alpha', 'Bravo', 'Charlie']); // back to asc
  });

  it('sortByEnabled_activeFirst', async () => {
    seed({
      windows: [
        mw({ id: 'a', name: 'Disabled One', isEnabled: false }),
        mw({ id: 'b', name: 'Enabled One', isEnabled: true }),
      ],
    });
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Enabled One')).toBeInTheDocument());
    const names = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-semibold')?.textContent?.trim());
    expect(names()).toEqual(['Disabled One', 'Enabled One']); // default name asc
    fireEvent.click(screen.getByRole('button', { name: /^Enabled$/ }));
    expect(names()).toEqual(['Enabled One', 'Disabled One']); // active first on asc
    fireEvent.click(screen.getByRole('button', { name: /^Enabled$/ }));
    expect(names()).toEqual(['Disabled One', 'Enabled One']); // desc
  });

  it('sortByTargets_globalLast', async () => {
    seed({
      windows: [
        mw({ id: 'g', name: 'Global Win', scopeKind: 'Global', targets: [] }),
        mw({
          id: 't2', name: 'Two Targets', scopeKind: 'Workflows',
          targets: [{ targetKind: 'Workflow', targetId: 'wf-1' }, { targetKind: 'Workflow', targetId: 'wf-2' }],
        }),
        mw({
          id: 't1', name: 'One Target', scopeKind: 'Workflows',
          targets: [{ targetKind: 'Workflow', targetId: 'wf-1' }],
        }),
      ],
      workflows: [{ id: 'wf-1', name: 'W1' }, { id: 'wf-2', name: 'W2' }],
    });
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Global Win')).toBeInTheDocument());
    const names = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-semibold')?.textContent?.trim());
    expect(names()).toEqual(['Global Win', 'One Target', 'Two Targets']); // default name asc
    fireEvent.click(screen.getByRole('button', { name: /^Targets$/ }));
    // asc by effective target count; Global ("all") is Infinity → last: One(1), Two(2), Global
    expect(names()).toEqual(['One Target', 'Two Targets', 'Global Win']);
  });
});
