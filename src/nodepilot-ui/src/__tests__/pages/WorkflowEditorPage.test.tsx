import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { useDesignStore } from '../../stores/designStore';

const signalRMock = vi.hoisted(() => ({
  handlers: {} as Record<string, ((payload: unknown) => void)[]>,
  connection: null as { stop: ReturnType<typeof vi.fn>; invoke: ReturnType<typeof vi.fn> } | null,
}));

// Mock SignalR before the page imports it - the editor opens a hub connection on mount
// and we don't want a real WebSocket attempt in the test runner.
vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      signalRMock.handlers = {};
      const connection = {
        on: (event: string, handler: (payload: unknown) => void) => {
          signalRMock.handlers[event] = [...(signalRMock.handlers[event] ?? []), handler];
        },
        onreconnected: vi.fn(),
        invoke: vi.fn(() => Promise.resolve()),
        start: () => Promise.resolve(),
        stop: vi.fn(() => Promise.resolve()),
      };
      signalRMock.connection = connection;
      return connection;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 1 } };
});

// `html-to-image` (used by the PNG-export button) tries to read CSS that jsdom doesn't
// provide. The button is never clicked during smoke tests, but the static import must
// not blow up at module-load time.
vi.mock('html-to-image', () => ({ toPng: () => Promise.resolve('data:image/png;base64,') }));

// ELK is loaded as a worker-backed bundle; the module fails to load in jsdom because
// of its Web Worker dependency. autoLayoutELK is only invoked behind a button so we
// can stub the whole module.
vi.mock('elkjs', () => ({ default: class { layout() { return Promise.resolve({}); } } }));

import { WorkflowEditorPage } from '../../pages/WorkflowEditorPage';
import { useAuthStore } from '../../stores/authStore';
import { COMPLETED_EXECUTION_TTL_MS } from '../../hooks/useSignalR';

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return {
    ...actual,
    useNavigate: () => navigateMock,
    // useBlocker requires a data router (createBrowserRouter). In tests we use MemoryRouter,
    // so we stub it to always return an unblocked state — the blocker logic is tested via
    // integration (manual browser navigation), not unit tests.
    useBlocker: () => ({ state: 'unblocked' as const, proceed: vi.fn(), reset: vi.fn() }),
  };
});

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
// Cancel-paths override per test via vi.mocked(confirmDialog).mockResolvedValueOnce(false).
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';
import { useToastStore } from '../../stores/toastStore';

// Stable test user-id so the auth-store + workflow lock-owner match deterministically.
const TEST_USER_ID = '11111111-1111-1111-1111-111111111111';

// Default mock: locked-by-test-user so Save/Publish/Tidy buttons are visible. The lock-by-me
// state is the "I am editing" path covered by most toolbar tests; lock-free state is exercised
// in the Bearbeiten-button test below.
const MOCK_WORKFLOW = {
  id: 'wf-smoke-1',
  name: 'Smoke Workflow',
  description: 'For tests',
  isEnabled: false,
  version: 1,
  definitionJson: JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 100, y: 100 },
        data: { label: 'Trigger', activityType: 'manualTrigger', config: {} } },
      { id: 'step-b', type: 'activity', position: { x: 400, y: 100 },
        // targetMachineId is set so the pre-publish lint stays clean — required since the
        // Publish-button now routes through PrePublishChecklistModal, which would otherwise
        // intercept the click on a runScript missing its machine.
        data: { label: 'Run', activityType: 'runScript', targetMachineId: 'machine-test', config: { script: 'Get-PSDrive C' } } },
    ],
    edges: [{ id: 'e1', source: 'step-a', target: 'step-b' }],
  }),
  createdAt: '2026-04-26T12:00:00Z',
  updatedAt: '2026-04-26T12:00:00Z',
  checkedOutByUserId: TEST_USER_ID,
  checkedOutByUserName: 'tester',
  checkedOutAt: '2026-04-26T12:30:00Z',
};

// Disabled + unlocked: used by the lock-flow tests (Bearbeiten-button visibility).
const MOCK_DISABLED = { ...MOCK_WORKFLOW, isEnabled: false, checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null };

// Productive: enabled + no lock — used by the Publish/Disable-toggle tests.
const MOCK_PRODUCTIVE = { ...MOCK_WORKFLOW, isEnabled: true, checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null };

// Disabled + foreign lock — used by the lock-by-other state tests for the toggle.
const MOCK_LOCKED_BY_OTHER = {
  ...MOCK_WORKFLOW,
  isEnabled: false,
  checkedOutByUserId: '99999999-9999-9999-9999-999999999999',
  checkedOutByUserName: 'someone-else',
  checkedOutAt: '2026-04-26T12:30:00Z',
};

const EMPTY_WORKFLOW = {
  ...MOCK_WORKFLOW,
  definitionJson: JSON.stringify({ nodes: [], edges: [] }),
};

const server = setupServer(
  http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_WORKFLOW)),
  http.get(`${BASE}/api/workflows`, () => HttpResponse.json([{ id: 'wf-smoke-1', name: 'Smoke Workflow' }])),
  http.get(`${BASE}/api/machines`, () => HttpResponse.json([])),
  http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])),
  http.get(`${BASE}/api/executions`, () => HttpResponse.json([])),
  http.get(`${BASE}/api/observability/config`, () =>
    HttpResponse.json({ enabled: false, browserOtlpEndpoint: null, serviceName: null, environment: 'dev' })
  ),
  http.get(/\/api\/workflows\/.+\/step-health/, () => HttpResponse.json({})),
  http.get(/\/api\/workflows\/.+\/step-stats/, () => HttpResponse.json({})),
  http.get(/\/api\/workflows\/.+\/folder/, () => HttpResponse.json(null)),
  http.get(/\/api\/shared-workflow-folders/, () => HttpResponse.json([])),
  http.get(/\/api\/workflows\/.+\/versions/, () => HttpResponse.json([])),
  http.get(/\/api\/global-variables/, () => HttpResponse.json([])),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

beforeEach(() => {
  navigateMock.mockReset();
  signalRMock.handlers = {};
  signalRMock.connection = null;
  useDesignStore.setState({ designerMode: 'expert' });
  useToastStore.setState({ toasts: [] });
});

function emitSignalR(event: string, payload: unknown) {
  for (const handler of signalRMock.handlers[event] ?? []) handler(payload);
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ userId: TEST_USER_ID, username: 'tester', role, isAuthenticated: true });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/workflows/wf-smoke-1']}>
        <Routes>
          <Route path="/workflows/:id" element={<WorkflowEditorPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

// The toolbar action buttons (Publish / Test / Disable …) render as soon as the workflow query
// resolves — they are gated on role + lock, NOT on the canvas. The canvas nodes hydrate from
// definitionJson in a SEPARATE effect a tick later. Clicking a graph-dependent action before
// hydration makes prePublishLint / run() see an empty graph (no-trigger) and take the wrong
// branch — opening the pre-publish checklist modal or no-opping instead of firing the mutation —
// which then silently times out. Waiting only for the workflow-name input is NOT enough (nodes
// hydrate a tick after it). Under v8 coverage on the 2-core CI runner this race loses almost
// every time, flaking a rotating 3-5 of these tests each run. Gate graph-dependent interactions
// on a rendered canvas node (mirrors the node-context-menu test further down).
async function waitForCanvasReady() {
  await waitFor(() => expect(document.querySelector('.react-flow__node')).not.toBeNull());
}

// The expert-mode inspect/layout/export actions (Find&Replace, Diff, Dry-Run, Keyboard
// shortcuts, Tidy/Restore, Export JSON/PNG) now live inside the "Werkzeuge" (Tools) popover
// instead of being individual toolbar buttons. Open it before querying those rows. The menu
// stays open across stateful clicks (tidy/restore), so one call suffices per test.
async function openToolsMenu() {
  fireEvent.click(await screen.findByTestId('tools-menu-trigger'));
}

describe('WorkflowEditorPage — smoke + toolbar', () => {
  it('mounts and renders save + publish buttons after workflow load', async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByTitle(/Save in place|Zwischen-Speichern/i)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Republish|Publish/ })).toBeInTheDocument();
    });
  });

  // The `np-designer` class on the editor root is the load-bearing hook for the
  // dark-mode orange accent (index.css scopes `html.dark .np-designer { … }` to it
  // and shields the React Flow canvas). If it ever gets dropped, the whole designer
  // chrome silently falls back to the cold-blue base accent — so pin its presence.
  it('roots the editor in the .np-designer scope (dark-mode orange accent hook)', async () => {
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(container.querySelector('.np-designer')).not.toBeNull();
  });

  // The Atelier design language rides two class hooks: `.wd-atelier` on the editor root
  // (scopes the designer-atelier.css token re-declaration) and `.wd-atelier-on` on <html>
  // (re-tokenises body-portaled tooltips that escape the scope). The suite-wide setup pins
  // classic, so this test flips the store explicitly and checks both directions.
  it('applies + removes the Atelier scope classes with designStore.designerTheme', async () => {
    useDesignStore.setState({ designerTheme: 'atelier' });
    const { container, unmount } = renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(container.querySelector('.np-designer.wd-atelier')).not.toBeNull();
    expect(document.documentElement.classList.contains('wd-atelier-on')).toBe(true);

    act(() => { useDesignStore.setState({ designerTheme: 'classic' }); });
    await waitFor(() => expect(container.querySelector('.np-designer.wd-atelier')).toBeNull());
    expect(document.documentElement.classList.contains('wd-atelier-on')).toBe(false);

    // Unmount must clean the <html> marker so other routes never render "atelier-tinted".
    useDesignStore.setState({ designerTheme: 'atelier' });
    await waitFor(() => expect(document.documentElement.classList.contains('wd-atelier-on')).toBe(true));
    unmount();
    expect(document.documentElement.classList.contains('wd-atelier-on')).toBe(false);
  });

  it('populates the workflow name input with the loaded workflow name', async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument();
    });
  });

  it('renders Test and Debug execution buttons for an Admin user', async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Debug/ })).toBeInTheDocument();
    });
  });

  it('undo/redo buttons start disabled with empty history', async () => {
    renderPage();
    await waitFor(() => {
      const undo = screen.getByTitle(/Undo/i) as HTMLButtonElement;
      const redo = screen.getByTitle(/Redo/i) as HTMLButtonElement;
      expect(undo).toBeDisabled();
      expect(redo).toBeDisabled();
    });
  });

  it('renders Search, Find&Replace, Diff buttons in toolbar', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Search nodes/)).toBeInTheDocument());
    await openToolsMenu();
    expect(screen.getByTitle(/Find & Replace/)).toBeInTheDocument();
    expect(screen.getByTitle(/Diff against/)).toBeInTheDocument();
  });

  it('renders Help (?) and Dry-Run buttons', async () => {
    renderPage();
    await openToolsMenu();
    expect(screen.getByTitle(/Keyboard shortcuts/)).toBeInTheDocument();
    expect(screen.getByTitle(/Dry-?[rR]un/)).toBeInTheDocument();
  });

  it('renders Tidy + Orig buttons for Admin (canWrite)', async () => {
    renderPage('Admin');
    await openToolsMenu();
    expect(screen.getByTitle(/Layout:.*click to apply/)).toBeInTheDocument();
    expect(screen.getByTitle(/Restore layout/)).toBeInTheDocument();
  });

  it('Orig button is disabled before any tidy operation', async () => {
    renderPage('Admin');
    await openToolsMenu();
    expect(screen.getByTitle(/Restore layout/)).toBeDisabled();
  });

  it('Zoom-to-selection button is disabled when nothing is selected', async () => {
    renderPage();
    await openToolsMenu();
    expect(screen.getByTitle(/Zoom to selection/)).toBeDisabled();
  });
});

describe('WorkflowEditorPage — RBAC', () => {
  it('Viewer sees read-only banner', async () => {
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.getByText(/Read-only/)).toBeInTheDocument();
    expect(screen.getByText(/Viewer role|Viewer-Rolle/i)).toBeInTheDocument();
  });

  it('Viewer does not see Test or Debug buttons', async () => {
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^Test/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Debug/ })).not.toBeInTheDocument();
  });

  it('Viewer does not see Save / Publish / Tidy buttons', async () => {
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByTitle(/Save in place|Zwischen-Speichern/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Republish|Publish/ })).not.toBeInTheDocument();
    expect(screen.queryByTitle(/Layout:.*click to apply/)).not.toBeInTheDocument();
  });

  it('Operator sees Test + Save + Publish (same edit affordances as Admin)', async () => {
    renderPage('Operator');
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument();
      expect(screen.getByTitle(/Save in place|Zwischen-Speichern/i)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Republish|Publish/ })).toBeInTheDocument();
    });
  });

  it('Admin does NOT see read-only banner', async () => {
    renderPage('Admin');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByText(/Read-only/)).not.toBeInTheDocument();
  });

  // Canvas "New Workflow" pill (top-right overlay) — gated on roleCanWrite, so Admin/Operator
  // see it, Viewer does not. Same role gate as Save/Publish.
  it('Admin sees the canvas New Workflow pill', async () => {
    renderPage('Admin');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument();
  });

  it('Viewer does not see the canvas New Workflow pill', async () => {
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByTitle(/Neuer Workflow|New Workflow/i)).not.toBeInTheDocument();
  });

  it('Operator sees the canvas New Workflow pill (same edit affordance as Admin)', async () => {
    renderPage('Operator');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument();
  });
});

describe('WorkflowEditorPage — Workflow data variations', () => {
  it('locked-by-me workflow shows Publish button (no Republish label)', async () => {
    // Default MOCK_WORKFLOW is locked-by-test-user → Save+Publish are visible. With the new
    // lock model, Publish always reads "Publish" (no Republish — the workflow is always
    // disabled while locked, so it's always re-enabling on publish).
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Republish/ })).not.toBeInTheDocument();
  });

  it('unlocked disabled workflow shows BOTH Bearbeiten and Publish buttons', async () => {
    // Iteration 2: the Publish button is visible whenever roleCanWrite, not just lock-by-me.
    // On a disabled+unlocked workflow it routes to /enable (re-publish without editing).
    // Bearbeiten remains the entry point for actual editing.
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_DISABLED)));
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Edit$/i })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument();
    // Save still requires lock-by-me — not visible without an active edit lock.
    expect(screen.queryByTitle(/Save in place|Speichern/)).not.toBeInTheDocument();
  });

  it('productive workflow shows Disable button (no Publish, no Bearbeiten yet because Bearbeiten still appears)', async () => {
    // The Publish/Disable-Toggle reads "Disable" when the workflow is productive. Bearbeiten
    // also appears as the lock entry-point — both coexist in the header. There is NO button
    // labelled "Publish" while the workflow is currently enabled.
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)));
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /Disable/ })).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^Publish$/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Edit$/i })).toBeInTheDocument();
  });

  it('disabled + locked-by-other shows Publish but disabled', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)));
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument());
    const publishBtn = screen.getByRole('button', { name: /^Publish$/ }) as HTMLButtonElement;
    expect(publishBtn).toBeDisabled();
    // Tooltip surfaces the lock owner so the user knows why the action is unavailable.
    expect(publishBtn.title).toMatch(/someone-else|editing|bearbeitet/i);
  });

  it('empty workflow disables PNG export button', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(EMPTY_WORKFLOW)));
    renderPage();
    await openToolsMenu();
    expect(screen.getByTitle('Export as PNG')).toBeDisabled();
  });

  it('JSON export button is enabled when workflowId is present', async () => {
    renderPage();
    await openToolsMenu();
    expect(screen.getByTitle('Export as JSON')).not.toBeDisabled();
  });
});

describe('WorkflowEditorPage — Sidebar (Node Library / Workflows tabs)', () => {
  it('defaults to Workflows tab; switching to Nodes shows Node Library', async () => {
    renderPage();
    // Default is Workflows tab — the node-search input (Nodes-tab-only) is NOT visible
    await waitFor(() => expect(screen.queryByPlaceholderText('Search nodes...')).not.toBeInTheDocument());

    // Switching to Nodes reveals the node library (its search input)
    fireEvent.click(screen.getByRole('button', { name: /Nodes/ }));
    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());
  });

  it('switches to Workflows tab and back to Nodes tab', async () => {
    renderPage();
    // Start on Workflows (default), switch to Nodes
    fireEvent.click(screen.getByRole('button', { name: /Nodes/ }));
    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());

    // Switch back to Workflows — node library (its search input) disappears
    fireEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => expect(screen.queryByPlaceholderText('Search nodes...')).not.toBeInTheDocument());
  });

  it('collapse button hides the sidebar content', async () => {
    renderPage();
    // Navigate to Nodes tab first so we have something visible to collapse
    fireEvent.click(screen.getByRole('button', { name: /Nodes/ }));
    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Collapse sidebar'));

    expect(screen.queryByPlaceholderText('Search nodes...')).not.toBeInTheDocument();
    expect(screen.getByTitle('Expand sidebar')).toBeInTheDocument();
  });

  it('expand button restores the sidebar', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: /Nodes/ }));
    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Collapse sidebar'));
    fireEvent.click(screen.getByTitle('Expand sidebar'));

    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());
  });

  it('search input filters node library by typing', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: /Nodes/ }));
    await waitFor(() => expect(screen.getByPlaceholderText('Search nodes...')).toBeInTheDocument());

    fireEvent.change(screen.getByPlaceholderText('Search nodes...'), { target: { value: 'definitelynotreal' } });

    // Most categories vanish; this is a smoke check that filter reaches the render path.
    // We can't easily check exact category absence without knowing the catalog, so we just
    // verify the search input retained the value.
    expect((screen.getByPlaceholderText('Search nodes...') as HTMLInputElement).value).toBe('definitelynotreal');
  });
});

describe('WorkflowEditorPage — Header name editing', () => {
  it('typing in the name input updates it (and marks dirty)', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    const nameInput = screen.getByDisplayValue('Smoke Workflow') as HTMLInputElement;
    fireEvent.change(nameInput, { target: { value: 'Renamed' } });

    expect(nameInput.value).toBe('Renamed');
  });
});

describe('WorkflowEditorPage — Mutations', () => {
  it('Save button posts to PUT /workflows/{id}', async () => {
    let putCalled = false;
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, async () => {
        putCalled = true;
        return HttpResponse.json(MOCK_WORKFLOW);
      })
    );
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Save in place|Zwischen-Speichern/i)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Save in place|Zwischen-Speichern/i));

    await waitFor(() => expect(putCalled).toBe(true));
  });

  it('manual Save failure shows an error toast instead of silently leaving the dirty dot', async () => {
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () =>
        new HttpResponse(JSON.stringify({ message: 'definitionJson is structurally invalid' }), { status: 400 })
      ),
    );
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Broken Workflow' } });
    fireEvent.click(screen.getByTitle(/Save in place|Zwischen-Speichern/i));

    await waitFor(() =>
      expect(useToastStore.getState().toasts.some((toast) =>
        toast.kind === 'error' && /structurally invalid/i.test(toast.message),
      )).toBe(true));
  });

  it('Publish button posts to atomic /publish endpoint', async () => {
    // Publish now hits a single atomic endpoint instead of PUT + /enable. The default
    // MOCK_WORKFLOW is locked-by-test-user, so the Publish button is visible.
    let publishCalled = false;
    server.use(
      http.post(`${BASE}/api/workflows/wf-smoke-1/publish`, () => {
        publishCalled = true;
        return HttpResponse.json({ ...MOCK_WORKFLOW, isEnabled: true, checkedOutByUserId: null });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /^Publish$/ }));

    await waitFor(() => expect(publishCalled).toBe(true));
  });

  it('Disable-Toggle on productive workflow posts to /disable', async () => {
    // Iteration 2: clicking the Disable button (the toggle's productive-state label) hits the
    // existing /disable endpoint — kill-switch path, no lock interaction.
    let disableCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/disable`, () => {
        disableCalled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    // The "Workflow stoppen?" prompt is auto-confirmed by the confirmDialog mock (resolves true).
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /Disable/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Disable/ }));

    await waitFor(() => expect(disableCalled).toBe(true));
  });

  it('Publish-Toggle on disabled+unlocked workflow posts to /enable (no lock needed)', async () => {
    // Iteration 2: on a disabled workflow without an active lock, Publish routes to /enable
    // directly — the user can re-publish without going through Bearbeiten first.
    let enableCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_DISABLED)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/enable`, () => {
        enableCalled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /^Publish$/ }));

    await waitFor(() => expect(enableCalled).toBe(true));
  });

  it('Publish click on a workflow with lint warnings opens the pre-publish modal instead of mutating', async () => {
    // Workflow with a remote fileOperation missing targetMachineId - that's a lint ERROR, so the
    // pre-publish modal should intercept and the /enable mutation must NOT fire until the
    // user explicitly confirms (which they can't with errors anyway, but absence-of-call is
    // the assertion here).
    let enableCalled = false;
    const dirtyDef = JSON.stringify({
      nodes: [
        { id: 'step-a', type: 'activity', position: { x: 100, y: 100 },
          data: { label: 'Trigger', activityType: 'manualTrigger', config: {} } },
        { id: 'step-b', type: 'activity', position: { x: 400, y: 100 },
          data: { label: 'Delete file', activityType: 'fileOperation', config: { operation: 'delete', path: 'C:\\temp\\x.txt' } } }, // no machine
      ],
      edges: [{ id: 'e1', source: 'step-a', target: 'step-b' }],
    });
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () =>
        HttpResponse.json({ ...MOCK_DISABLED, definitionJson: dirtyDef }),
      ),
      http.post(`${BASE}/api/workflows/wf-smoke-1/enable`, () => {
        enableCalled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Publish$/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^Publish$/ }));

    // Modal opens with the missing-target-machine error
    await waitFor(() => expect(screen.getByText(/Pre-publish check/i)).toBeInTheDocument());
    expect(enableCalled).toBe(false);

    // Cancel closes it without calling /enable
    fireEvent.click(screen.getByText('Cancel'));
    await waitFor(() => expect(screen.queryByText(/Pre-publish check/i)).not.toBeInTheDocument());
    expect(enableCalled).toBe(false);
  });

  it('Bearbeiten button posts to /lock', async () => {
    let lockCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_DISABLED)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/lock`, () => {
        lockCalled = true;
        return HttpResponse.json({ ...MOCK_DISABLED, checkedOutByUserId: TEST_USER_ID });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Edit$/i })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^Edit$/i }));

    await waitFor(() => expect(lockCalled).toBe(true));
  });

  it('canvas New Workflow pill posts a new workflow and navigates into it', async () => {
    // Clicking the top-right "Neuer Workflow" pill opens a name popover; Enter submits a
    // POST /workflows (empty definition, current workflow's folderId) and navigates to the
    // new id — same create path as the Workflows list page, reachable from the canvas.
    let createBody: Record<string, unknown> | null = null;
    server.use(
      http.post(`${BASE}/api/workflows`, async ({ request }) => {
        createBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ id: 'wf-new-1', name: createBody.name, definitionJson: '{"nodes":[],"edges":[]}' });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Neuer Workflow|New Workflow/i));
    const nameInput = await waitFor(() => screen.getByTestId('new-workflow-name-input'));
    fireEvent.change(nameInput, { target: { value: 'Freshly Created' } });
    fireEvent.keyDown(nameInput, { key: 'Enter' });

    await waitFor(() => expect(createBody).not.toBeNull());
    expect(createBody).toMatchObject({ name: 'Freshly Created', description: '' });
    expect(JSON.parse(createBody!.definitionJson as string)).toEqual({ nodes: [], edges: [] });
    expect(navigateMock).toHaveBeenCalledWith('/workflows/wf-new-1');
  });

  it('New Workflow create forwards the current workflow folderId (RBAC R3)', async () => {
    // The current workflow's folderId must be carried into the POST body — otherwise a
    // Folder-scoped Editor creating from inside /Finance would hit 403 (server defaults to
    // Root where they have no edit). Override the GET to return a folderId and assert it flows.
    let createBody: Record<string, unknown> | null = null;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json({ ...MOCK_WORKFLOW, folderId: 'folder-42' })),
      http.post(`${BASE}/api/workflows`, async ({ request }) => {
        createBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ id: 'wf-new-1', name: createBody.name, definitionJson: '{"nodes":[],"edges":[]}' });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Neuer Workflow|New Workflow/i));
    const nameInput = await waitFor(() => screen.getByTestId('new-workflow-name-input'));
    fireEvent.change(nameInput, { target: { value: 'Folder Child' } });
    fireEvent.keyDown(nameInput, { key: 'Enter' });
    await waitFor(() => expect(createBody).not.toBeNull());
    expect(createBody!.folderId).toBe('folder-42');
  });

  it('New Workflow create is disabled until a name is entered', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Neuer Workflow|New Workflow/i));
    await waitFor(() => screen.getByTestId('new-workflow-name-input'));
    const createBtn = screen.getByRole('button', { name: /Anlegen|Create/i });
    expect(createBtn).toBeDisabled();
    fireEvent.change(screen.getByTestId('new-workflow-name-input'), { target: { value: 'Named' } });
    expect(createBtn).not.toBeDisabled();
  });

  it('New Workflow popover Escape closes without creating or navigating', async () => {
    let postCalled = false;
    server.use(
      http.post(`${BASE}/api/workflows`, () => {
        postCalled = true;
        return HttpResponse.json({ id: 'wf-new-1', name: 'x', definitionJson: '{"nodes":[],"edges":[]}' });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Neuer Workflow|New Workflow/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Neuer Workflow|New Workflow/i));
    const nameInput = await waitFor(() => screen.getByTestId('new-workflow-name-input'));
    fireEvent.change(nameInput, { target: { value: 'Will not be created' } });
    fireEvent.keyDown(nameInput, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByTestId('new-workflow-name-input')).not.toBeInTheDocument());
    expect(postCalled).toBe(false);
    expect(navigateMock).not.toHaveBeenCalled();
  });
});

describe('WorkflowEditorPage — Search overlay', () => {
  it('clicking Search button opens the SearchOverlay', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/Search nodes \(Ctrl\+F\)/)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Search nodes \(Ctrl\+F\)/));

    // SearchOverlay renders a search input. It uses placeholder text — we look for any input
    // appearing after click that wasn't there before.
    await waitFor(() => {
      const inputs = screen.getAllByRole('textbox');
      // sidebar has 2 inputs (workflow name + library search) initially. The overlay adds 1.
      expect(inputs.length).toBeGreaterThanOrEqual(3);
    });
  });
});

describe('WorkflowEditorPage — Help overlay', () => {
  it('clicking Help button opens the HelpOverlay', async () => {
    renderPage();
    await openToolsMenu();

    fireEvent.click(screen.getByTitle(/Keyboard shortcuts/));

    // HelpOverlay renders an h2 with "Keyboard shortcuts" — pin that.
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Keyboard shortcuts' })).toBeInTheDocument();
    });
  });
});

describe('WorkflowEditorPage — Find & Replace overlay', () => {
  it('clicking Find&Replace button opens the overlay with Find/Replace inputs', async () => {
    renderPage();
    await openToolsMenu();

    fireEvent.click(screen.getByTitle(/Find & Replace/));

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/Find/)).toBeInTheDocument();
      expect(screen.getByPlaceholderText(/Replace with/)).toBeInTheDocument();
    });
  });
});

describe('WorkflowEditorPage — Diff modal', () => {
  it('clicking Diff button opens the WorkflowDiffModal', async () => {
    renderPage();
    await openToolsMenu();

    fireEvent.click(screen.getByTitle(/Diff against/));

    await waitFor(() => expect(screen.getByText(/Workflow Diff/)).toBeInTheDocument());
  });
});

describe('WorkflowEditorPage — Lint warning button', () => {
  it('lint summary button appears when workflow has issues (e.g. empty runScript)', async () => {
    // The mock workflow has a runScript node with non-empty `script` config — should be lint-clean.
    // To trigger a lint issue, give the workflow an emailNotification missing required to/subject.
    const lintBadWorkflow = {
      ...MOCK_WORKFLOW,
      definitionJson: JSON.stringify({
        nodes: [{
          id: 'step-bad', type: 'activity', position: { x: 0, y: 0 },
          data: { label: 'Send', activityType: 'emailNotification', config: {} },
        }],
        edges: [],
      }),
    };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(lintBadWorkflow)));
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // The lint summary button has title with "errors, warnings"
    await waitFor(() => {
      expect(screen.getByTitle(/errors, .* warnings/)).toBeInTheDocument();
    });
  });
});

describe('WorkflowEditorPage — Replay banner', () => {
  it('initially does not show replay banner', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByText(/Replay-Modus/)).not.toBeInTheDocument();
  });

  it('shows a dismissible test-path banner after a designer run completes', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () => HttpResponse.json({
        id: 'exec-1',
        workflowId: 'wf-smoke-1',
        status: 'Pending',
        startedAt: '2026-04-26T12:00:00Z',
        completedAt: null,
        triggeredBy: 'manual',
        errorMessage: null,
        traceId: null,
        spanId: null,
        returnData: null,
        inputParametersJson: null,
      }, { status: 202 })),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));
    await waitFor(() => expect(signalRMock.connection?.invoke).toHaveBeenCalledWith('JoinWorkflow', 'wf-smoke-1'));

    act(() => {
      emitSignalR('LiveEventsBatch', {
        Events: [
          {
            Type: 'StepStarted',
            Event: {
              executionId: 'exec-1',
              workflowId: 'wf-smoke-1',
              stepId: 'step-a',
              stepName: 'Trigger',
              stepType: 'manualTrigger',
              startedAt: '2026-04-26T12:00:00Z',
            },
          },
          {
            Type: 'StepCompleted',
            Event: {
              executionId: 'exec-1',
              workflowId: 'wf-smoke-1',
              stepId: 'step-a',
              stepName: 'Trigger',
              status: 'Succeeded',
              completedAt: '2026-04-26T12:00:01Z',
            },
          },
          {
            Type: 'ExecutionStatusChanged',
            Event: {
              executionId: 'exec-1',
              workflowId: 'wf-smoke-1',
              status: 'Succeeded',
              completedAt: '2026-04-26T12:00:01Z',
            },
          },
        ],
      });
    });

    await waitFor(() => expect(screen.getByText(/Test trace/i)).toBeInTheDocument());

    fireEvent.click(screen.getByLabelText(/Hide test trace/i));

    await waitFor(() => expect(screen.queryByText(/Test trace/i)).not.toBeInTheDocument());
  });

  it('Test-Verlauf banner persists after Live View TTL eviction (canvasExecutionSnapshot fix)', async () => {
    // Regression guard for cbaa6aa: the canvas snapshot keeps effectiveCanvasExecution
    // non-null after the 30 s TTL evicts the execution from liveExecutionsById. Without the
    // fix, canvasLiveExecution → null at eviction time would (in a refactored world) clear
    // canvasRunIsTerminalState and drop the banner prematurely.
    //
    // We switch to fake timers *after* the page is set up so MSW + React Query work
    // normally, but *before* emitting SignalR events so the debounce (100 ms) and eviction
    // (30 s) timers are fake and can be advanced instantly.
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () =>
        HttpResponse.json({
          id: 'exec-snap',
          workflowId: 'wf-smoke-1',
          status: 'Pending',
          startedAt: '2026-04-26T12:00:00Z',
          completedAt: null,
          triggeredBy: 'manual',
          errorMessage: null,
          traceId: null,
          spanId: null,
          returnData: null,
          inputParametersJson: null,
        }, { status: 202 }),
      ),
    );

    // Phase 1 — real timers: render and click Test so MSW / React Query play nicely.
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));
    await waitFor(() =>
      expect(signalRMock.connection?.invoke).toHaveBeenCalledWith('JoinWorkflow', 'wf-smoke-1'),
    );

    // Phase 2 — fake timers: emit events AFTER switching so scheduleEviction's setTimeout
    // is intercepted by Vitest. This lets us advance past the 30 s TTL without a real wait.
    vi.useFakeTimers();
    try {
      act(() => {
        emitSignalR('LiveEventsBatch', {
          Events: [
            {
              Type: 'StepStarted',
              Event: {
                executionId: 'exec-snap',
                workflowId: 'wf-smoke-1',
                stepId: 'step-a',
                stepName: 'Trigger',
                stepType: 'manualTrigger',
                startedAt: '2026-04-26T12:00:00Z',
              },
            },
            {
              Type: 'StepCompleted',
              Event: {
                executionId: 'exec-snap',
                workflowId: 'wf-smoke-1',
                stepId: 'step-a',
                stepName: 'Trigger',
                status: 'Succeeded',
                completedAt: '2026-04-26T12:00:01Z',
              },
            },
            {
              Type: 'ExecutionStatusChanged',
              Event: {
                executionId: 'exec-snap',
                workflowId: 'wf-smoke-1',
                status: 'Succeeded',
                completedAt: '2026-04-26T12:00:01Z',
              },
            },
          ],
        });
      });

      // Fire the 100 ms debounce inside useSignalR and flush React state.
      await act(async () => { vi.advanceTimersByTime(150); });
      expect(screen.getByText(/Test trace/i)).toBeInTheDocument();

      // Jump past the TTL. The execution is evicted from liveExecutionsById so
      // canvasLiveExecution becomes null. canvasExecutionSnapshot must bridge the gap:
      //   effectiveCanvasExecution = canvasLiveExecution ?? canvasExecutionSnapshot
      // keeps the value non-null, canvasRunIsTerminalState stays true, banner survives.
      await act(async () => { vi.advanceTimersByTime(COMPLETED_EXECUTION_TTL_MS + 100); });
      expect(screen.getByText(/Test trace/i)).toBeInTheDocument();

      // clearDesignerCanvasHighlight (X-button) resets all three states: executionId,
      // terminalState, and the snapshot — banner goes away.
      fireEvent.click(screen.getByLabelText(/Hide test trace/i));
      expect(screen.queryByText(/Test trace/i)).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('WorkflowEditorPage — Empty workflow edge cases', () => {
  it('empty workflow renders without crashing and disables PNG export', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(EMPTY_WORKFLOW)));
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    await openToolsMenu();
    expect(screen.getByTitle('Export as PNG')).toBeDisabled();
  });

  it('workflow with malformed definitionJson loads as empty', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () =>
        HttpResponse.json({ ...MOCK_WORKFLOW, definitionJson: '{not json' })
      )
    );
    renderPage();
    // Should still render the toolbar, no throw
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
  });
});

describe('WorkflowEditorPage — Lock-State Banners', () => {
  it('LockedByOther banner shows owner name', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/Editing in progress/i)).toBeInTheDocument());
    // Owner name appears both in the banner and in the header lock-pill — accept ≥1.
    expect(screen.getAllByText('someone-else').length).toBeGreaterThanOrEqual(1);
  });

  it('LockedByMe banner explains Save / Publish / Beenden', async () => {
    // Default MOCK_WORKFLOW is locked-by-test-user.
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/You are editing/i)).toBeInTheDocument());
  });

  it('Unlocked + productive workflow shows yellow "running productive" banner', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/running productive/i)).toBeInTheDocument());
  });

  it('Unlocked + disabled workflow shows yellow "Workflow is disabled" banner', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_DISABLED)));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/Workflow is disabled/i)).toBeInTheDocument());
  });

  it('Force-Unlock button visible for Admin on locked-by-other workflow', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Force Unlock/ })).toBeInTheDocument());
  });

  it('Force-Unlock button NOT visible for Operator on locked-by-other workflow', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)));
    renderPage('Operator');
    await waitFor(() => expect(screen.getByText(/Editing in progress/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Force Unlock/ })).not.toBeInTheDocument();
  });
});

describe('WorkflowEditorPage — Lock Mutations', () => {
  it('Beenden button (locked-by-me, no dirty changes) posts to /unlock without confirm', async () => {
    let unlockCalled = false;
    server.use(
      http.post(`${BASE}/api/workflows/wf-smoke-1/unlock`, () => {
        unlockCalled = true;
        return HttpResponse.json(MOCK_DISABLED);
      })
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /^End/i })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^End/i }));

    await waitFor(() => expect(unlockCalled).toBe(true));
  });

  it('Force-Unlock button posts to /force-unlock when confirmed', async () => {
    // confirmDialog mock resolves true by default (user confirms).
    let forceUnlockCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/force-unlock`, () => {
        forceUnlockCalled = true;
        return HttpResponse.json(MOCK_DISABLED);
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Force Unlock/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Force Unlock/ }));

    await waitFor(() => expect(forceUnlockCalled).toBe(true));
  });

  it('Force-Unlock button does NOT call endpoint when confirm is cancelled', async () => {
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    let forceUnlockCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_LOCKED_BY_OTHER)),
      http.post(`${BASE}/api/workflows/wf-smoke-1/force-unlock`, () => {
        forceUnlockCalled = true;
        return HttpResponse.json(MOCK_DISABLED);
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Force Unlock/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Force Unlock/ }));

    await new Promise((r) => setTimeout(r, 30));
    expect(forceUnlockCalled).toBe(false);
  });
});

describe('WorkflowEditorPage — Right Sidebar (Properties / EdgeProperties / BulkEdit)', () => {
  /**
   * Trick: ReactFlow honors the initial selected:true on nodes/edges in its store. We pre-select
   * via the workflow JSON so onSelectionChange fires on mount and the page renders the right pane.
   */

  it('selecting a single node renders PropertiesPanel', async () => {
    const wfWithSelectedNode = {
      ...MOCK_WORKFLOW,
      definitionJson: JSON.stringify({
        nodes: [
          { id: 'step-a', type: 'activity', position: { x: 100, y: 100 }, selected: true,
            data: { label: 'Selected', activityType: 'log', config: { message: 'hi' } } },
        ],
        edges: [],
      }),
    };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfWithSelectedNode)));
    renderPage();
    // The PropertiesPanel renders an outputVariable input with placeholder = node.id.
    await waitFor(() => {
      // PanelHeader renders the activity label; this proves Properties pane mounted.
      expect(screen.getAllByText(/Selected/).length).toBeGreaterThan(0);
    });
  });

  it('selecting an edge renders EdgePropertiesPanel', async () => {
    const wfWithSelectedEdge = {
      ...MOCK_WORKFLOW,
      definitionJson: JSON.stringify({
        nodes: [
          { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A', activityType: 'log' } },
          { id: 'b', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'B', activityType: 'log' } },
        ],
        edges: [{ id: 'e1', source: 'a', target: 'b', selected: true, data: { label: 'On Success', condition: 'a.success' } }],
      }),
    };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfWithSelectedEdge)));
    renderPage();
    // EdgePropertiesPanel renders a "Connection" header.
    await waitFor(() => expect(screen.getByText('Connection')).toBeInTheDocument());
  });

  it('selecting ≥2 nodes renders BulkEditPanel', async () => {
    const wfWithTwoSelected = {
      ...MOCK_WORKFLOW,
      definitionJson: JSON.stringify({
        nodes: [
          { id: 'a', type: 'activity', position: { x: 0, y: 0 }, selected: true, data: { label: 'A', activityType: 'log' } },
          { id: 'b', type: 'activity', position: { x: 200, y: 0 }, selected: true, data: { label: 'B', activityType: 'log' } },
        ],
        edges: [],
      }),
    };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfWithTwoSelected)));
    renderPage();
    // BulkEditPanel renders an "X activities selected" header.
    await waitFor(() => expect(screen.getByText(/activities selected/)).toBeInTheDocument());
  });

  it('no selection → neither panel renders', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());
    expect(screen.queryByText('Connection')).not.toBeInTheDocument();
    expect(screen.queryByText(/activities selected/)).not.toBeInTheDocument();
  });
});

describe('WorkflowEditorPage — Tidy Layout', () => {
  /**
   * Designer-Store layoutMode persists across tests via Zustand. We reset it before each
   * Tidy test so assertions on cycle position are deterministic.
   */
  beforeEach(async () => {
    const { useDesignStore } = await import('../../stores/designStore');
    useDesignStore.setState({ layoutMode: 'LR' });
  });

  it('clicking Tidy enables the Orig button (positions snapshot stored)', async () => {
    renderPage('Admin');
    await openToolsMenu();
    await waitFor(() => expect(screen.getByTitle(/Layout: LR/)).toBeInTheDocument());

    expect(screen.getByTitle(/Restore layout/)).toBeDisabled();

    fireEvent.click(screen.getByTitle(/Layout: LR/));

    await waitFor(() => expect(screen.getByTitle(/Restore layout/)).not.toBeDisabled());
  });

  it('Tidy cycles layout mode label LR → TB on second use', async () => {
    renderPage('Admin');
    await openToolsMenu();
    await waitFor(() => expect(screen.getByTitle(/Layout: LR/)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Layout: LR/));
    await waitFor(() => expect(screen.getByTitle(/Layout: TB/)).toBeInTheDocument());
  });

  it('Orig button restores after Tidy (resets isDisabled state)', async () => {
    renderPage('Admin');
    await openToolsMenu();
    await waitFor(() => expect(screen.getByTitle(/Layout: LR/)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Layout: LR/));
    await waitFor(() => expect(screen.getByTitle(/Restore layout/)).not.toBeDisabled());

    fireEvent.click(screen.getByTitle(/Restore layout/));
    // After restore, the Orig button disables again — origLayoutRef cleared.
    await waitFor(() => expect(screen.getByTitle(/Restore layout/)).toBeDisabled());
  });

  it('saving after a workflow layout change posts the current definition', async () => {
    let savedDefinition: { nodes: { id: string; position: { x: number; y: number } }[] } | null = null;
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, async ({ request }) => {
        const body = (await request.json()) as { definitionJson: string };
        savedDefinition = JSON.parse(body.definitionJson);
        return HttpResponse.json(MOCK_WORKFLOW);
      }),
    );

    renderPage('Admin');
    await openToolsMenu();
    await waitFor(() => expect(screen.getByTitle(/Layout: LR/)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Layout: LR/));
    await waitFor(() => expect(screen.getByTitle(/Restore layout/)).not.toBeDisabled());
    await waitFor(() => expect(screen.getByTitle(/Unsaved changes|Ungespeicherte Änderungen/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Save in place|Zwischen-Speichern/i));

    await waitFor(() => expect(savedDefinition).not.toBeNull());
    const savedById = new Map(savedDefinition!.nodes.map((n) => [n.id, n]));
    expect([...savedById.keys()]).toEqual(['step-a', 'step-b']);
    expect(
      savedById.get('step-a')!.position.x !== 100
      || savedById.get('step-a')!.position.y !== 100
      || savedById.get('step-b')!.position.x !== 400
      || savedById.get('step-b')!.position.y !== 100,
    ).toBe(true);
  });
});

describe('WorkflowEditorPage — Overlays via Buttons & Keys', () => {
  it('Help opens via "?" key (no Ctrl)', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.keyDown(window, { key: '?' });

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Keyboard shortcuts' })).toBeInTheDocument();
    });
  });

  it('Help closes via Escape', async () => {
    renderPage();
    await openToolsMenu();

    fireEvent.click(screen.getByTitle(/Keyboard shortcuts/));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Keyboard shortcuts' })).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Keyboard shortcuts' })).not.toBeInTheDocument());
  });

  it('Quick Switcher opens via Ctrl+P', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'p', ctrlKey: true });

    // WorkflowQuickSwitcher renders a search input with this placeholder
    await waitFor(() => expect(screen.getByPlaceholderText(/Switch to workflow/)).toBeInTheDocument());
  });

  it('Command Palette opens via Ctrl+Shift+P', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'P', ctrlKey: true, shiftKey: true });

    // CommandPalette renders a "Type a command…" placeholder
    await waitFor(() => expect(screen.getByPlaceholderText(/Type a command/)).toBeInTheDocument());
  });

  it('Search opens via Ctrl+F', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'f', ctrlKey: true });

    // SearchOverlay adds a third textbox to the page (sidebar already has 2 inputs)
    await waitFor(() => {
      const inputs = screen.getAllByRole('textbox');
      expect(inputs.length).toBeGreaterThanOrEqual(3);
    });
  });
});

describe('WorkflowEditorPage — Keyboard Shortcuts', () => {
  it('Ctrl+H opens Find & Replace overlay', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'h', ctrlKey: true });

    await waitFor(() => expect(screen.getByPlaceholderText(/Replace with/)).toBeInTheDocument());
  });

  it('F11 toggles fullscreen (read-only banner disappears in fullscreen mode)', async () => {
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText(/Read-only/)).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'F11' });

    // In fullscreen, the read-only banner is suppressed (gated on !fullscreen).
    await waitFor(() => expect(screen.queryByText(/Read-only/)).not.toBeInTheDocument());
    // The "Exit (F11)" pill appears in the canvas.
    expect(screen.getByTitle(/Exit fullscreen/)).toBeInTheDocument();
  });

  it('Ctrl+Shift+E zoom-to-selection shortcut does not crash with no selection', async () => {
    renderPage('Admin');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // No nodes selected → zoom-to-selection is a no-op but must not throw.
    fireEvent.keyDown(window, { key: 'E', ctrlKey: true, shiftKey: true });

    expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument();
  });

  it('Escape closes the FindReplace overlay (priority over Search/Help)', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // Open both Find&Replace and Help — Escape should close Find&Replace first.
    fireEvent.keyDown(window, { key: 'h', ctrlKey: true });
    await waitFor(() => expect(screen.getByPlaceholderText(/Replace with/)).toBeInTheDocument());

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByPlaceholderText(/Replace with/)).not.toBeInTheDocument());
  });

  it('Ctrl+Z / Ctrl+Y do not throw with empty history', async () => {
    renderPage('Admin');
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // Both shortcuts are no-ops on an empty history but the handlers must run cleanly.
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    fireEvent.keyDown(window, { key: 'y', ctrlKey: true });

    expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument();
  });
});

// Note: a Cancel-running-execution test was attempted but skipped — toggling the
// useWorkflowSignalR mock at runtime via vi.resetModules + dynamic import triggered
// "Maximum update depth exceeded" inside the @xyflow/react store wiring. The button
// is only ~10 lines of code in EditorHeader and is exercised in EditorHeader-level tests.

// The dedicated "cycle-only" banner was removed: roots are trigger-only, so a graph with no
// (enabled) trigger — including a cycle-only graph — now surfaces the always-on `no-trigger` lint
// error instead (covered by workflowLint.test.ts + e2e/error-handling.spec.ts 7.3).

describe('WorkflowEditorPage — RunWorkflowDialog (manualTrigger params)', () => {
  it('clicking Test on a workflow with manualTrigger parameters opens RunWorkflowDialog', async () => {
    const wfWithParams = {
      ...MOCK_WORKFLOW,
      isEnabled: true,
      checkedOutByUserId: TEST_USER_ID,
      checkedOutByUserName: 'tester',
      checkedOutAt: '2026-04-26T12:30:00Z',
      definitionJson: JSON.stringify({
        nodes: [
          { id: 'mt', type: 'activity', position: { x: 0, y: 0 },
            data: {
              label: 'Manual',
              activityType: 'manualTrigger',
              config: {
                title: 'Run with params',
                parameters: [{ name: 'env', type: 'string', required: true, default: 'dev' }],
              },
            },
          },
        ],
        edges: [],
      }),
    };
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfWithParams)),
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfWithParams)),
      http.get(/\/api\/executions\?workflowId/, () => HttpResponse.json([])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));

    // RunWorkflowDialog renders the trigger title + parameter inputs.
    await waitFor(() => expect(screen.getByText(/Run with params/)).toBeInTheDocument());
  });

  it('Test on productive (enabled, unlocked) workflow calls /execute without PUT save', async () => {
    // Regression: handleRunClick used to call saveMutation unconditionally, which 423'd on
    // read-only workflows because PUT requires the edit-lock. /execute itself has no lock
    // check — the user must be able to start a run on a published workflow without first
    // checking it out.
    let putCalled = false;
    let executeCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_PRODUCTIVE)),
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () => {
        putCalled = true;
        return new HttpResponse(JSON.stringify({ message: 'Workflow is not checked out for editing' }), { status: 423 });
      }),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () => {
        executeCalled = true;
        return HttpResponse.json({ executionId: 'exec-1' }, { status: 202 });
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));

    await waitFor(() => expect(executeCalled).toBe(true));
    expect(putCalled).toBe(false);
  });

  it('Test on disabled workflow shows a toast and does not open dialog', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(MOCK_DISABLED)));

    // Force into edit mode so the Test button is visible.
    const wfLockedDisabled = { ...MOCK_DISABLED, checkedOutByUserId: TEST_USER_ID, checkedOutByUserName: 'tester', checkedOutAt: '2026-04-26T12:30:00Z' };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(wfLockedDisabled)));

    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));

    await waitFor(() =>
      expect(useToastStore.getState().toasts.some((toast) => /disabled/i.test(toast.message))).toBe(true));
  });
});

describe('WorkflowEditorPage — DesignToggle + ActivityTypeFilter', () => {
  beforeEach(async () => {
    // Reset design store flags so each test starts from a known baseline.
    const { useDesignStore } = await import('../../stores/designStore');
    useDesignStore.setState({
      machineColoringEnabled: false,
      failureHeatmapEnabled: false,
      edgesAnimated: true,
    });
  });

  it('Heatmap toggle in the view-overlays popover flips the store flag', async () => {
    renderPage('Admin');
    // The failure-heatmap switch row lives inside the "Ansicht" popover since the toolbar redesign.
    fireEvent.click(await screen.findByTestId('view-overlays-trigger'));
    const heatmapBtn = await screen.findByTestId('toggle-failure-heatmap');

    const { useDesignStore } = await import('../../stores/designStore');
    expect(useDesignStore.getState().failureHeatmapEnabled).toBe(false);

    fireEvent.click(heatmapBtn);

    expect(useDesignStore.getState().failureHeatmapEnabled).toBe(true);
  });

  it('Activity-Type-Filter button is visible in toolbar', async () => {
    renderPage('Admin');
    await waitFor(() => expect(screen.getByTitle('Filter activity types')).toBeInTheDocument());
  });
});

describe('WorkflowEditorPage — Lint Panel toggle', () => {
  it('clicking lint summary button opens the LintPanel', async () => {
    const lintBadWorkflow = {
      ...MOCK_WORKFLOW,
      definitionJson: JSON.stringify({
        nodes: [{
          id: 'step-bad', type: 'activity', position: { x: 0, y: 0 },
          data: { label: 'Send', activityType: 'emailNotification', config: {} },
        }],
        edges: [],
      }),
    };
    server.use(http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(lintBadWorkflow)));
    renderPage();
    await waitFor(() => expect(screen.getByTitle(/errors, .* warnings/)).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/errors, .* warnings/));

    // LintPanel renders an h3 "Workflow validation"
    await waitFor(() => expect(screen.getByText(/Workflow validation/)).toBeInTheDocument());
  });
});

// ---------------------------------------------------------------------------
// Characterization tests (pre-refactor safety net for the frontend large-file
// split). These pin externally-observable behaviour the rest of the suite does
// NOT cover — autosave timing, save-before-run incl. the save-failure abort,
// and the beforeunload guard — so the upcoming useWorkflowPersistence /
// useWorkflowExecution extractions can be verified to preserve it.
// ---------------------------------------------------------------------------
describe('WorkflowEditorPage — characterization (autosave + run + unload)', () => {
  // Fake timers leak across tests if a test throws before restoring; reset defensively.
  afterEach(() => { vi.useRealTimers(); });

  it('does not autosave on initial load (no PUT within the 5s window)', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let putCalled = false;
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, async () => { putCalled = true; return HttpResponse.json(MOCK_WORKFLOW); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // Load resets isDirty=false → the autosave effect arms no timer. Advancing past
    // the 5s debounce must not produce a save.
    await act(async () => { vi.advanceTimersByTime(6000); });

    expect(putCalled).toBe(false);
  });

  it('autosaves 5s after a dirty name edit', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let putCalled = false;
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, async () => { putCalled = true; return HttpResponse.json(MOCK_WORKFLOW); }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Renamed' } });
    await act(async () => { vi.advanceTimersByTime(5000); });

    await waitFor(() => expect(putCalled).toBe(true));
  });

  it('Test on a dirty, writable workflow saves (PUT) before calling /execute', async () => {
    const lockedEnabled = { ...MOCK_WORKFLOW, isEnabled: true }; // locked-by-test-user + enabled
    const order: string[] = [];
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(lockedEnabled)),
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () => { order.push('put'); return HttpResponse.json(lockedEnabled); }),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () => { order.push('execute'); return HttpResponse.json({ executionId: 'e1' }, { status: 202 }); }),
      http.get(/\/api\/executions\?workflowId/, () => HttpResponse.json([])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));

    await waitFor(() => expect(order).toEqual(['put', 'execute']));
  });

  it('Ctrl+Enter on a dirty workflow saves (PUT) before calling /execute', async () => {
    // Regression: the keyboard handler (triggerTest) memoizes the `run` it closes over.
    // When `run` was unmemoized + excluded from triggerTest's deps, editing after mount
    // left a stale `run` (isDirty=false) bound to the shortcut — Ctrl+Enter skipped the
    // pre-run save and tested the server's old definition. Editing here AFTER mount, without
    // touching role/run-status, is exactly the scenario that exposed the stale closure.
    const lockedEnabled = { ...MOCK_WORKFLOW, isEnabled: true }; // locked-by-test-user + enabled
    const order: string[] = [];
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(lockedEnabled)),
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () => { order.push('put'); return HttpResponse.json(lockedEnabled); }),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () => { order.push('execute'); return HttpResponse.json({ executionId: 'e1' }, { status: 202 }); }),
      http.get(/\/api\/executions\?workflowId/, () => HttpResponse.json([])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Renamed' } });
    fireEvent.keyDown(window, { key: 'Enter', ctrlKey: true });

    await waitFor(() => expect(order).toEqual(['put', 'execute']));
  });

  it('Test does not start /execute when the pre-run save fails', async () => {
    const lockedEnabled = { ...MOCK_WORKFLOW, isEnabled: true };
    let executeCalled = false;
    server.use(
      http.get(`${BASE}/api/workflows/wf-smoke-1`, () => HttpResponse.json(lockedEnabled)),
      http.put(`${BASE}/api/workflows/wf-smoke-1`, () => new HttpResponse(JSON.stringify({ message: 'boom' }), { status: 500 })),
      http.post(`${BASE}/api/workflows/wf-smoke-1/execute`, () => { executeCalled = true; return HttpResponse.json({ executionId: 'e1' }, { status: 202 }); }),
      http.get(/\/api\/executions\?workflowId/, () => HttpResponse.json([])),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /Test/ })).toBeInTheDocument());

    await waitForCanvasReady();
    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: /Test/ }));

    await waitFor(() =>
      expect(useToastStore.getState().toasts.some((toast) => toast.kind === 'error')).toBe(true));
    expect(useToastStore.getState().toasts.filter((toast) => toast.kind === 'error')).toHaveLength(1);
    expect(executeCalled).toBe(false);
  });

  it('beforeunload prevents default only when there are unsaved changes', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue('Smoke Workflow')).toBeInTheDocument());

    // Clean state → handler must not cancel the unload.
    const cleanEvt = new Event('beforeunload', { cancelable: true });
    const cleanPrevent = vi.spyOn(cleanEvt, 'preventDefault');
    globalThis.dispatchEvent(cleanEvt);
    expect(cleanPrevent).not.toHaveBeenCalled();

    // Dirty state → handler cancels (browser shows the leave-confirmation).
    fireEvent.change(screen.getByDisplayValue('Smoke Workflow'), { target: { value: 'Renamed' } });
    const dirtyEvt = new Event('beforeunload', { cancelable: true });
    const dirtyPrevent = vi.spyOn(dirtyEvt, 'preventDefault');
    globalThis.dispatchEvent(dirtyEvt);
    expect(dirtyPrevent).toHaveBeenCalled();
  });
});

describe('WorkflowEditorPage — node context menu preserves node data', () => {
  // Regression: the right-click context menu's disable/enable (and breakpoint) toggles call
  // handleNodeDataUpdate with a PARTIAL patch (e.g. { disabled: true }), but the handler used
  // to REPLACE the node's entire `data`. Toggling disable then enable therefore wiped
  // activityType/config/label, producing a definition the backend rejects on save
  // (400 "data.activityType is required") — exactly the "save button doesn't work after
  // disabling a node" symptom. The handler now merges the patch onto the existing data.
  it('disabling then re-enabling a node via the context menu keeps its type/config on save', async () => {
    let savedDefinition: { nodes: { id: string; data: Record<string, unknown> }[] } | null = null;
    server.use(
      http.put(`${BASE}/api/workflows/wf-smoke-1`, async ({ request }) => {
        const body = (await request.json()) as { definitionJson: string };
        savedDefinition = JSON.parse(body.definitionJson);
        return HttpResponse.json(MOCK_WORKFLOW);
      }),
    );

    const { container } = renderPage('Admin');
    await waitFor(() => expect(screen.getByTitle(/Save in place|Zwischen-Speichern/i)).toBeInTheDocument());
    // The runScript node ('step-b') must be mounted before we can right-click it.
    await waitFor(() => {
      if (!container.querySelector('.react-flow__node[data-id="step-b"]')) throw new Error('node not rendered yet');
    });
    const nodeEl = () => container.querySelector('.react-flow__node[data-id="step-b"]') as HTMLElement;

    // Right-click → "Disable step".
    fireEvent.contextMenu(nodeEl());
    fireEvent.click(await screen.findByText('Disable step'));

    // Right-click again → the menu now offers "Enable step" (re-query: the node re-rendered).
    fireEvent.contextMenu(nodeEl());
    fireEvent.click(await screen.findByText('Enable step'));

    // Save in place.
    fireEvent.click(screen.getByTitle(/Save in place|Zwischen-Speichern/i));

    await waitFor(() => expect(savedDefinition).not.toBeNull());
    const stepB = savedDefinition!.nodes.find((n) => n.id === 'step-b');
    expect(stepB).toBeDefined();
    // Core of the regression: the merge must preserve the node's identity fields.
    expect(stepB!.data.activityType).toBe('runScript');
    expect(stepB!.data.label).toBe('Run');
    expect((stepB!.data.config as Record<string, unknown>).script).toBe('Get-PSDrive C');
    // And the disable→enable round-trip left it enabled again.
    expect(stepB!.data.disabled).toBe(false);
  });
});
