import { describe, it, expect, beforeAll, afterAll, afterEach, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { WorkflowsPage } from '../../pages/WorkflowsPage';
import { useAuthStore } from '../../stores/authStore';
import { useToastStore } from '../../stores/toastStore';
import type { Workflow } from '../../types/api';

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

// Store-driven confirm replaces native confirm(); default-resolve true (user confirms).
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';

const server = setupServer(
  http.get(`${BASE}/api/workflows/folders`, () =>
    HttpResponse.json({ folders: [], assignments: [] })
  )
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

beforeEach(() => navigateMock.mockReset());

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'admin', role });
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <WorkflowsPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function mkWorkflow(overrides: Partial<Workflow> = {}): Workflow {
  return {
    id: 'wf-1',
    name: 'Workflow A',
    description: null,
    definitionJson: '{}',
    version: 1,
    isEnabled: true,
    createdAt: '2026-04-26T10:00:00Z',
    updatedAt: '2026-04-26T10:00:00Z',
    createdBy: 'admin',
    updatedBy: 'admin',
    triggerTypes: [],
    activityCount: 0,
    ...overrides,
  };
}

const WORKFLOW_WITH_SCHEDULE: Workflow = mkWorkflow({
  id: 'wf-1',
  name: 'Backup Workflow',
  description: 'Daily backup',
  definitionJson: JSON.stringify({
    nodes: [{ id: 't1', data: { activityType: 'scheduleTrigger', config: {} } }],
    edges: [],
  }),
  version: 3,
  isEnabled: true,
  triggerTypes: ['scheduleTrigger'],
  activityCount: 4,
  lastExecution: {
    id: 'exec-1', status: 'Succeeded',
    startedAt: new Date(Date.now() - 30_000).toISOString(),
    completedAt: new Date(Date.now() - 20_000).toISOString(),
    durationMs: 10_000,
  },
  successCount: 5,
  totalCount: 6,
  avgDurationMs: 8000,
});

const DISABLED_WORKFLOW: Workflow = mkWorkflow({
  id: 'wf-2',
  name: 'Disabled Job',
  isEnabled: false,
});

describe('WorkflowsPage — basics', () => {
  it('shows loading state initially', () => {
    server.use(http.get(`${BASE}/api/workflows`, () => new Promise(() => {})));
    renderPage();
    expect(screen.getByText(/loading/i)).toBeInTheDocument();
  });

  it('renders workflow names', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([WORKFLOW_WITH_SCHEDULE, DISABLED_WORKFLOW])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());
    expect(screen.getByText('Disabled Job')).toBeInTheDocument();
  });

  it('shows Schedule trigger badge', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Schedule')).toBeInTheDocument());
  });

  it('shows manualTrigger badge with Hand icon label', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([mkWorkflow({ triggerTypes: ['manualTrigger'] })])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Manual')).toBeInTheDocument());
  });

  it('shows version number', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByText(/v3/)).toBeInTheDocument());
  });

  it('shows description when provided', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Daily backup')).toBeInTheDocument());
  });

  it('shows disabled state indicator', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([DISABLED_WORKFLOW]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Disabled Job')).toBeInTheDocument());
    expect(screen.getByText('Disabled')).toBeInTheDocument();
  });

  it('empty workflows list shows "No workflows yet"', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage();
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());
  });

  it('shows last run "just now" for recent execution', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE]))
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());
    expect(screen.getAllByText('just now').length).toBeGreaterThanOrEqual(1);
  });

  it('shows "never" when no last execution', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([mkWorkflow({ lastExecution: null })])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('never')).toBeInTheDocument());
  });
});

describe('WorkflowsPage — import folder targeting', () => {
  const ROOT = '00000000-0000-0000-0000-000000000001';
  const TEAM_A = '11111111-1111-1111-1111-111111111111';
  const caps = { canRead: true, canRun: true, canEdit: true, canAdmin: false };
  const folderRow = (id: string, name: string, parentFolderId: string | null, path: string, depth: number) => ({
    id, parentFolderId, name, path, depth,
    createdAt: '2026-01-01T00:00:00Z', createdByUserId: null, workflowCount: 0, capabilities: caps,
  });

  function seedImport(capture: { url: string | null }) {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/shared-workflow-folders`, () =>
        HttpResponse.json([
          folderRow(ROOT, 'Root', null, '/', 0),
          folderRow(TEAM_A, 'Team-A', ROOT, '/Team-A', 1),
        ])),
      http.post(`${BASE}/api/workflows/import`, ({ request }) => {
        capture.url = request.url;
        return HttpResponse.json({ created: 1, workflows: [], errors: [] });
      }),
    );
  }

  const envelopeFile = () => new File(
    [JSON.stringify({ schema: 'nodepilot-workflow-export/v1', exportVersion: 1, workflows: [] })],
    'wf.json', { type: 'application/json' });

  it('posts the import without folderId when no folder is selected', async () => {
    const capture = { url: null as string | null };
    seedImport(capture);
    const { container } = renderPage('Admin');
    await waitFor(() => expect(screen.queryByText(/loading/i)).not.toBeInTheDocument());

    const input = container.querySelector('input[accept="application/json,.json"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [envelopeFile()] } });

    await waitFor(() => expect(capture.url).not.toBeNull());
    expect(capture.url).not.toContain('folderId');
  });

  it('appends the selected folder as folderId to the import URL', async () => {
    const capture = { url: null as string | null };
    seedImport(capture);
    const { container } = renderPage('Admin');
    await screen.findByText('Team-A');
    fireEvent.click(screen.getByText('Team-A'));

    const input = container.querySelector('input[accept="application/json,.json"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [envelopeFile()] } });

    await waitFor(() => expect(capture.url).not.toBeNull());
    expect(capture.url).toContain(`folderId=${TEAM_A}`);
  });
});

describe('WorkflowsPage — import result toast', () => {
  beforeEach(() => useToastStore.setState({ toasts: [] }));

  function seedImportResponse(body: { created: number; workflows: unknown[]; errors: string[] }) {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/workflows/import`, () => HttpResponse.json(body)),
    );
  }

  const envelopeFile = (name = 'wf.json') => new File(
    [JSON.stringify({ schema: 'nodepilot-workflow-export/v1', exportVersion: 1, workflows: [] })],
    name, { type: 'application/json' });

  async function importFile(container: HTMLElement) {
    await waitFor(() => expect(screen.queryByText(/loading/i)).not.toBeInTheDocument());
    const input = container.querySelector('input[accept="application/json,.json"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [envelopeFile()] } });
  }

  it('shows a success toast when every file imported cleanly', async () => {
    seedImportResponse({ created: 2, workflows: [], errors: [] });
    const { container } = renderPage('Admin');
    await importFile(container);

    await waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(useToastStore.getState().toasts[0].kind).toBe('success');
  });

  it('shows a long-lived error toast when the import reports per-file errors', async () => {
    seedImportResponse({ created: 1, workflows: [], errors: ['workflow "X" is invalid'] });
    const { container } = renderPage('Admin');
    await importFile(container);

    await waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    const toastEntry = useToastStore.getState().toasts[0];
    expect(toastEntry.kind).toBe('error');
    expect(toastEntry.message).toContain('workflow "X" is invalid');
  });
});

describe('WorkflowsPage — RBAC', () => {
  it('shows Create Workflow button for Admin', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.queryByText(/loading/i)).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: /New Workflow/ })).toBeInTheDocument();
  });

  it('hides Create Workflow button for Viewer', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Viewer');
    await waitFor(() => expect(screen.queryByText(/loading/i)).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New Workflow/i })).not.toBeInTheDocument();
  });

  it('hides Import + SCOrch import buttons for Viewer', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Viewer');
    await waitFor(() => expect(screen.queryByText(/loading/i)).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^Import$/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Import SCOrch/ })).not.toBeInTheDocument();
  });

  it('Operator sees Run + Edit + Duplicate but not Delete', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage('Operator');
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());
    expect(screen.getByTitle('Run Now')).toBeInTheDocument();
    expect(screen.getByTitle('Edit')).toBeInTheDocument();
    expect(screen.getByTitle('Duplicate')).toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });

  it('Viewer sees only View (Pencil) + Export', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());
    expect(screen.queryByTitle('Run Now')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Duplicate')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
    // Pencil shows for everyone, but the title flips to "View" for non-canWrite roles
    expect(screen.getByTitle('View')).toBeInTheDocument();
    expect(screen.getByTitle('Export as JSON')).toBeInTheDocument();
  });
});

describe('WorkflowsPage — Create flow', () => {
  it('shows create form when New Workflow clicked', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Workflow/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New Workflow/ }));

    expect(screen.getByPlaceholderText('Workflow name…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeInTheDocument();
  });

  it('cancel button closes create form and clears name', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Workflow/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New Workflow/ }));
    fireEvent.change(screen.getByPlaceholderText('Workflow name…'), { target: { value: 'Foo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByPlaceholderText('Workflow name…')).not.toBeInTheDocument();
  });

  it('create button posts new workflow and navigates on success', async () => {
    let postBody: unknown = null;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/workflows`, async ({ request }) => {
        postBody = await request.json();
        return HttpResponse.json(mkWorkflow({ id: 'wf-new', name: 'Foo' }));
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Workflow/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New Workflow/ }));
    fireEvent.change(screen.getByPlaceholderText('Workflow name…'), { target: { value: 'Foo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-new'));
    expect((postBody as { name: string }).name).toBe('Foo');
  });

  it('Enter key in name input also creates the workflow', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/workflows`, () => HttpResponse.json(mkWorkflow({ id: 'wf-x' }))),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Workflow/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New Workflow/ }));
    const nameInput = screen.getByPlaceholderText('Workflow name…');
    fireEvent.change(nameInput, { target: { value: 'Foo' } });
    fireEvent.keyDown(nameInput, { key: 'Enter' });

    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-x'));
  });
});

describe('WorkflowsPage — Mutations', () => {
  it('clicking name button navigates to editor', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Backup Workflow'));
    expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-1');
  });

  it('Edit button navigates to editor', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Edit'));
    expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-1');
  });

  it('Run Now button on workflow without manualTrigger params posts /execute directly', async () => {
    let executeCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])),
      http.post(`${BASE}/api/workflows/wf-1/execute`, () => {
        executeCalled = true;
        return HttpResponse.json({ executionId: 'e-1' });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Run Now'));
    await waitFor(() => expect(executeCalled).toBe(true));
  });

  it('Run Now button on manualTrigger workflow with parameters opens RunWorkflowDialog', async () => {
    const wfWithManualParams = mkWorkflow({
      id: 'wf-manual',
      name: 'Manual WF',
      definitionJson: JSON.stringify({
        nodes: [{
          id: 'mt',
          data: {
            activityType: 'manualTrigger',
            config: {
              title: 'Run Manual',
              parameters: [{ name: 'env', type: 'string', required: true, default: 'dev' }],
            },
          },
        }],
        edges: [],
      }),
      triggerTypes: ['manualTrigger'],
    });
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([wfWithManualParams])));
    renderPage();
    await waitFor(() => expect(screen.getByText('Manual WF')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Run Now'));

    // RunWorkflowDialog renders the trigger title
    await waitFor(() => expect(screen.getByText(/Run Manual/)).toBeInTheDocument());
  });

  it('Power button on enabled workflow calls /disable', async () => {
    let disabled = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])),
      http.post(`${BASE}/api/workflows/wf-1/disable`, () => { disabled = true; return new HttpResponse(null, { status: 204 }); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Disable workflow'));
    await waitFor(() => expect(disabled).toBe(true));
  });

  it('Power button on disabled workflow calls /enable', async () => {
    let enabled = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([DISABLED_WORKFLOW])),
      http.post(`${BASE}/api/workflows/wf-2/enable`, () => { enabled = true; return new HttpResponse(null, { status: 204 }); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Disabled Job')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Enable workflow'));
    await waitFor(() => expect(enabled).toBe(true));
  });

  it('Duplicate button posts /duplicate', async () => {
    let duplicated = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])),
      http.post(`${BASE}/api/workflows/wf-1/duplicate`, () => {
        duplicated = true;
        return HttpResponse.json(mkWorkflow({ id: 'wf-1-dup' }));
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Duplicate'));
    await waitFor(() => expect(duplicated).toBe(true));
  });

  it('Delete button confirms then deletes', async () => {
    let deleted = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])),
      http.delete(`${BASE}/api/workflows/wf-1`, () => { deleted = true; return new HttpResponse(null, { status: 204 }); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Delete'));
    await waitFor(() => expect(deleted).toBe(true));
    expect(confirmDialog).toHaveBeenCalled();
  });

  it('Delete button does NOT delete when confirm is cancelled', async () => {
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    let deleted = false;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])),
      http.delete(`${BASE}/api/workflows/wf-1`, () => { deleted = true; return new HttpResponse(null, { status: 204 }); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Delete'));
    // Wait a tick to make sure no async happened
    await new Promise((r) => setTimeout(r, 20));
    expect(deleted).toBe(false);
  });
});

describe('WorkflowsPage — Sorting', () => {
  it('sorts workflows by name when Name header clicked', async () => {
    const wfA = mkWorkflow({ id: 'a', name: 'Alpha' });
    const wfB = mkWorkflow({ id: 'b', name: 'Bravo' });
    const wfC = mkWorkflow({ id: 'c', name: 'Charlie' });
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([wfC, wfA, wfB])));
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Name'));

    // After asc sort, Alpha should be first row, Charlie last
    const rows = screen.getAllByRole('row');
    // rows[0] is the header; data rows start at index 1
    expect(rows[1].textContent).toContain('Alpha');
    expect(rows[3].textContent).toContain('Charlie');
  });

  it('toggles sort direction on second click', async () => {
    const wfA = mkWorkflow({ id: 'a', name: 'Alpha' });
    const wfB = mkWorkflow({ id: 'b', name: 'Bravo' });
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([wfA, wfB])));
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeInTheDocument());

    // First click → asc, Alpha first
    fireEvent.click(screen.getByText('Name'));
    let rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('Alpha');

    // Second click → desc, Bravo first
    fireEvent.click(screen.getByText('Name'));
    rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('Bravo');
  });

  it('sorts by Activities count', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([
          mkWorkflow({ id: 'a', name: 'Few', activityCount: 1 }),
          mkWorkflow({ id: 'b', name: 'Many', activityCount: 10 }),
          mkWorkflow({ id: 'c', name: 'Some', activityCount: 5 }),
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Few')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Activities'));

    const rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('Few');
    expect(rows[3].textContent).toContain('Many');
  });

  it('sorts by Status (enabled first when desc)', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([
          mkWorkflow({ id: 'a', name: 'OffOne', isEnabled: false }),
          mkWorkflow({ id: 'b', name: 'OnOne', isEnabled: true }),
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('OffOne')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Status'));

    const rows = screen.getAllByRole('row');
    // Status sort uses Number(b.isEnabled) - Number(a.isEnabled) → enabled first asc
    expect(rows[1].textContent).toContain('OnOne');
  });

  it('sorts by Triggers (lexicographic by trigger-type composition)', async () => {
    // Seed in non-sorted order: webhook, schedule, manual. Asc key order is
    // manualTrigger < scheduleTrigger < webhookTrigger.
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([
          mkWorkflow({ id: 'w', name: 'W-WF', triggerTypes: ['webhookTrigger'] }),
          mkWorkflow({ id: 's', name: 'S-WF', triggerTypes: ['scheduleTrigger'] }),
          mkWorkflow({ id: 'm', name: 'M-WF', triggerTypes: ['manualTrigger'] }),
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('W-WF')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Triggers'));

    const rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('M-WF');
    expect(rows[3].textContent).toContain('W-WF');
  });

  it('sorts empty-trigger workflows first in asc Triggers sort', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([
          mkWorkflow({ id: 't', name: 'Has-Trig', triggerTypes: ['scheduleTrigger'] }),
          mkWorkflow({ id: 'e', name: 'Empty-Trig', triggerTypes: [] }),
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Has-Trig')).toBeInTheDocument());

    fireEvent.click(screen.getByText('Triggers'));

    const rows = screen.getAllByRole('row');
    // Empty composition ('') sorts before any non-empty key in asc order.
    expect(rows[1].textContent).toContain('Empty-Trig');
  });
});

describe('WorkflowsPage — Export & SCOrch result modal', () => {
  it('Export All button is disabled when there are no workflows', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Export All/ })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /Export All/ })).toBeDisabled();
  });

  it('Export All button is enabled when workflows present', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /Export All/ })).not.toBeDisabled();
  });
});

describe('WorkflowsPage — LastRunCell variants', () => {
  it.each([
    ['Failed', /Failed|just now/],
    ['Running', /just now/],
    ['Pending', /just now/],
    ['Cancelled', /just now/],
    ['Skipped', /just now/],
    ['Unknown', /just now/],
  ])('renders status %s without crashing', async (status, matcher) => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([
          mkWorkflow({
            id: 'wf-x', name: `WF-${status}`,
            lastExecution: {
              id: 'e1', status,
              startedAt: new Date(Date.now() - 30_000).toISOString(),
              completedAt: null, durationMs: null,
            },
          }),
        ])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText(`WF-${status}`)).toBeInTheDocument());
    expect(screen.getAllByText(matcher).length).toBeGreaterThan(0);
  });
});

describe('WorkflowsPage — SuccessRateCell color thresholds', () => {
  it('renders dash when no executions', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () =>
        HttpResponse.json([mkWorkflow({ successCount: 0, totalCount: 0 })])
      )
    );
    renderPage();
    await waitFor(() => expect(screen.getByText('Workflow A')).toBeInTheDocument());
    // The dash for "no rate" is rendered
    expect(screen.getAllByText('–').length).toBeGreaterThanOrEqual(1);
  });

  it('renders ratio "5/6" with colored bar when executions exist', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    renderPage();
    await waitFor(() => expect(screen.getByText('5/6')).toBeInTheDocument());
  });
});

describe('WorkflowsPage — Column resize', () => {
  it('mousedown on resize handle starts a resize loop without throwing', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([WORKFLOW_WITH_SCHEDULE])));
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Backup Workflow')).toBeInTheDocument());

    // Resize handles are absolute-positioned 1.5px-wide divs in <th>
    const handles = container.querySelectorAll('div.cursor-col-resize');
    expect(handles.length).toBeGreaterThanOrEqual(9);

    act(() => {
      fireEvent.mouseDown(handles[0], { clientX: 100 });
    });
    act(() => {
      fireEvent.mouseMove(window, { clientX: 200 });
    });
    act(() => {
      fireEvent.mouseUp(window);
    });

    // No throw, table still renders
    expect(screen.getByText('Backup Workflow')).toBeInTheDocument();
  });
});

describe('WorkflowsPage — AI workflow generation', () => {
  const SAMPLE_DEFINITION = JSON.stringify({
    nodes: [
      { id: 't', type: 'activity', data: { activityType: 'manualTrigger' } },
      { id: 's1', type: 'activity', data: { activityType: 'log' } },
    ],
    edges: [{ id: 'e1', source: 't', target: 's1' }],
  });

  it('KI-Generieren button visible for Admin (canWrite)', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /New AI Workflow/i })).toBeInTheDocument();
  });

  it('KI-Generieren button hidden for Viewer', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New AI Workflow/i })).not.toBeInTheDocument();
  });

  it('clicking KI-Generieren opens the WorkflowGenerationDialog', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New AI Workflow/i }));

    expect(screen.getByText(/Workflow per KI generieren/i)).toBeInTheDocument();
    expect(screen.getByLabelText('Workflow prompt')).toBeInTheDocument();
  });

  it('happy path: prompt → generate → preview → create → navigate', async () => {
    let createBody: Record<string, unknown> | null = null;
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/ai/generate-workflow`, () =>
        HttpResponse.json({
          definitionJson: SAMPLE_DEFINITION,
          suggestedName: 'Generated WF',
          suggestedDescription: 'Test',
          nodeCount: 2,
          edgeCount: 1,
          retried: false,
          durationMs: 100,
          model: 'fake-model',
        }),
      ),
      http.post(`${BASE}/api/workflows`, async ({ request }) => {
        createBody = await request.json() as Record<string, unknown>;
        return HttpResponse.json({
          id: 'wf-new', name: createBody.name, description: createBody.description,
          definitionJson: createBody.definitionJson, version: 1, isEnabled: false,
          createdAt: '2026-04-27T00:00:00Z', updatedAt: '2026-04-27T00:00:00Z',
          createdBy: 'admin', updatedBy: 'admin', triggerTypes: [], activityCount: 2,
        });
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New AI Workflow/i }));
    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'a workflow' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    await screen.findByText(/Generierten Workflow überprüfen/i);
    fireEvent.click(screen.getByRole('button', { name: /erstellen & öffnen/i }));

    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-new'));
    expect(createBody).toMatchObject({
      name: 'Generated WF',
      description: 'Test',
      definitionJson: SAMPLE_DEFINITION,
    });
  });

  it('backend 503 LLM_DISABLED surfaces as error in the prompt stage', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/ai/generate-workflow`, () =>
        HttpResponse.json(
          { code: 'LLM_DISABLED', message: 'AI assistant is disabled.' },
          { status: 503 },
        ),
      ),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No workflows yet/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /New AI Workflow/i }));
    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'a workflow' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    // Stays on prompt stage
    expect(screen.queryByText(/Generierten Workflow überprüfen/i)).not.toBeInTheDocument();
  });
});
