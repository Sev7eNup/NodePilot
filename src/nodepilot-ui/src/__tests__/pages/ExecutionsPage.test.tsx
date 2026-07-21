import { describe, it, expect, beforeAll, afterAll, afterEach, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { ExecutionsPage } from '../../pages/ExecutionsPage';
import { useAuthStore } from '../../stores/authStore';
import { confirmDialog } from '../../stores/confirmStore';
import type { WorkflowExecution, Workflow } from '../../types/api';

vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer(
  // Default: observability config returns disabled
  http.get(`${BASE}/api/observability/config`, () =>
    HttpResponse.json({ enabled: false, traceUiUrlTemplate: null, traceBackendName: null })
  )
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => {
  // Default to a Viewer so the role-gated retry button stays hidden unless a test opts in.
  useAuthStore.setState({ userId: 'u1', username: 'viewer', role: 'Viewer', isAuthenticated: true });
  vi.mocked(confirmDialog).mockClear().mockResolvedValue(true);
});
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ExecutionsPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const MOCK_EXECUTIONS: WorkflowExecution[] = [
  {
    id: 'exec-1',
    workflowId: 'wf-1',
    status: 'Succeeded',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    completedAt: new Date(Date.now() - 50_000).toISOString(),
    triggeredBy: 'manual',
    errorMessage: null,
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: null,
    startedByUsername: 'alice',
    stepsTotal: 3,
    stepsCompleted: 3,
    failedSteps: null,
  },
  {
    id: 'exec-2',
    workflowId: 'wf-2',
    status: 'Failed',
    startedAt: new Date(Date.now() - 120_000).toISOString(),
    completedAt: new Date(Date.now() - 110_000).toISOString(),
    triggeredBy: 'schedule',
    errorMessage: 'Script error',
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: null,
    startedByUsername: null,
    stepsTotal: 2,
    stepsCompleted: 1,
    failedSteps: [{ stepId: 'node-x', stepName: 'Broken Step' }],
  },
];

const MOCK_WORKFLOWS: Workflow[] = [
  { id: 'wf-1', name: 'Disk Check', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
  { id: 'wf-2', name: 'Backup Job', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
];

function mockList() {
  server.use(
    http.get(`${BASE}/api/executions`, () => HttpResponse.json(MOCK_EXECUTIONS)),
    http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK_WORKFLOWS))
  );
}

describe('ExecutionsPage', () => {
  it('shows loading state initially', () => {
    server.use(
      http.get(`${BASE}/api/executions`, () => new Promise(() => {})),
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([]))
    );
    renderPage();
    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('shows empty state when no executions', async () => {
    server.use(
      http.get(`${BASE}/api/executions`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([]))
    );
    renderPage();
    await waitFor(() =>
      expect(screen.getByText(/No executions yet/i)).toBeInTheDocument()
    );
  });

  it('renders execution rows with workflow names', async () => {
    // Workflow names appear both as a row toggle button and as a dropdown <option>;
    // query the button role so we assert on the row, not the filter dropdown.
    mockList();
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Backup Job' })).toBeInTheDocument();
  });

  it('renders status badges', async () => {
    mockList();
    renderPage();
    // "Succeeded"/"Failed" also appear in the summary + filter chips; scope to the table.
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());
    const table = within(screen.getByRole('table'));
    expect(table.getByText('Succeeded')).toBeInTheDocument();
    expect(table.getByText('Failed')).toBeInTheDocument();
  });

  it('renders the summary chips', async () => {
    mockList();
    renderPage();
    // Total = 2, with one succeeded + one failed.
    await waitFor(() => expect(screen.getByText('Total')).toBeInTheDocument());
    expect(screen.getByText('Success rate')).toBeInTheDocument();
  });

  it('renders the steps progress and failed-step count', async () => {
    mockList();
    renderPage();
    await waitFor(() => expect(screen.getByText('3/3')).toBeInTheDocument());
    expect(screen.getByText('1/2')).toBeInTheDocument();
    // The failed branch surfaces a "1 failed" chip.
    expect(screen.getByText('1 failed')).toBeInTheDocument();
  });

  it('expands a row when clicked and loads steps', async () => {
    mockList();
    server.use(
      http.get(`${BASE}/api/executions/exec-1/steps`, () =>
        HttpResponse.json([
          {
            id: 's1', stepId: 'node-check-disk', stepName: 'Check Disk', stepType: 'runScript',
            targetMachine: null, status: 'Succeeded',
            startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
            output: 'Disk OK', errorOutput: null, traceOutput: null,
            outputParametersJson: JSON.stringify({ freeGb: '42', status: 'ok' }),
          },
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());

    // Click the workflow-name toggle button for the first row.
    fireEvent.click(screen.getByRole('button', { name: 'Disk Check' }));

    await waitFor(() => expect(screen.getByText('node-check-disk')).toBeInTheDocument());
    expect(screen.getByText('Disk OK')).toBeInTheDocument();
    expect(screen.getByText('Output parameters')).toBeInTheDocument();
    expect(screen.getByText(/"freeGb": "42"/)).toBeInTheDocument();
    expect(screen.getByText(/"status": "ok"/)).toBeInTheDocument();
  });

  it('collapses an expanded row when clicked again', async () => {
    mockList();
    server.use(
      http.get(`${BASE}/api/executions/exec-1/steps`, () => HttpResponse.json([]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());

    const toggle = screen.getByRole('button', { name: 'Disk Check' });
    fireEvent.click(toggle); // expand
    await waitFor(() => expect(screen.getByText(/No steps executed/i)).toBeInTheDocument());
    fireEvent.click(toggle); // collapse
    await waitFor(() =>
      expect(screen.queryByText(/No steps executed/i)).not.toBeInTheDocument()
    );
  });

  it('shows the trigger label for an execution', async () => {
    mockList();
    renderPage();
    // TriggerCell maps 'manual' → 'Manual'; unknown raw values (here 'schedule') pass through.
    await waitFor(() => expect(screen.getByText('Manual')).toBeInTheDocument());
    expect(screen.getByText('schedule')).toBeInTheDocument();
  });

  it('filters by status via the quick-filter chips', async () => {
    mockList();
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());

    // Click the "Failed" status chip — only the failed run should remain in the table.
    fireEvent.click(screen.getByRole('button', { name: 'Failed' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Disk Check' })).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Backup Job' })).toBeInTheDocument();
  });

  it('filters by free-text search', async () => {
    mockList();
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());

    fireEvent.change(screen.getByPlaceholderText(/Search workflow/i), { target: { value: 'backup' } });
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Disk Check' })).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Backup Job' })).toBeInTheDocument();
  });

  it('hides the retry button for Viewers', async () => {
    mockList();
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disk Check' })).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Retry execution/i })).not.toBeInTheDocument();
  });

  it('retries an execution for Operators', async () => {
    useAuthStore.setState({ userId: 'u2', username: 'op', role: 'Operator', isAuthenticated: true });
    mockList();
    let retried = '';
    server.use(
      http.post(`${BASE}/api/executions/:id/retry`, ({ params }) => {
        retried = params.id as string;
        return HttpResponse.json({ id: 'exec-new', workflowId: 'wf-2', status: 'Pending' });
      })
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Backup Job' })).toBeInTheDocument());

    const retryButtons = screen.getAllByRole('button', { name: /Retry execution/i });
    expect(retryButtons.length).toBe(2);
    // Backup Job (exec-2) is the second data row.
    fireEvent.click(retryButtons[1]);
    await waitFor(() => expect(retried).toBe('exec-2'));
  });

  it('applies the ?status=Failed deep-link on mount (dashboard "Show failed" shortcut)', async () => {
    mockList();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    patchFetch();
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={['/executions?status=Failed']}>
          <ExecutionsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    // The toolbar (with status filter chips) renders once the list has loaded.
    const failedBtn = await waitFor(() => screen.getByRole('button', { name: /^failed$/i }));
    // The Failed chip is the active one (bg-primary), proving the param was read on mount.
    expect(failedBtn).toHaveClass('bg-primary');
    expect(failedBtn).not.toHaveClass('bg-surface-high');
  });
});
