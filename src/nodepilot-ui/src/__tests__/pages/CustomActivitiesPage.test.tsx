import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { CustomActivitiesPage } from '../../pages/CustomActivitiesPage';
import { useAuthStore } from '../../stores/authStore';
import { useToastStore } from '../../stores/toastStore';
import { confirmDialog } from '../../stores/confirmStore';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});

const BASE = 'http://localhost';

// authedFetch prefixes every call with the relative '/api'. jsdom needs an absolute URL,
// so rewrite leading-'/' paths onto BASE — same shim MaintenanceWindowsPage.test uses.
function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer();
beforeAll(() => {
  server.listen({ onUnhandledRequest: 'warn' });
  // CodeMirror's measure cycle calls Range.getClientRects/getBoundingClientRect, which
  // jsdom doesn't implement — shim them so the rAF measure pass doesn't throw. Vitest
  // isolates test files, so this prototype patch stays local to this file's environment.
  const rect = { x: 0, y: 0, top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, toJSON() { return this; } } as DOMRect;
  Range.prototype.getBoundingClientRect = () => rect;
  Range.prototype.getClientRects = () =>
    ({ length: 0, item: () => null, *[Symbol.iterator]() {} }) as unknown as DOMRectList;
});
beforeEach(() => {
  vi.mocked(confirmDialog).mockClear().mockResolvedValue(true);
  useToastStore.setState({ toasts: [] });
});
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

type EntryOverrides = Partial<Record<string, unknown>>;
const entry = (overrides: EntryOverrides = {}) => ({
  id: 'ca-1',
  key: 'disk-check',
  type: 'custom:disk-check',
  name: 'Disk Check',
  description: null,
  icon: 'extension',
  color: null,
  runsRemote: false,
  timeout: 'always',
  inputs: [],
  outputs: [],
  isEnabled: false,
  version: 3,
  ...overrides,
});

const VERSIONS = [
  {
    version: 2, name: 'Disk Check', description: null, engine: 'auto', runsRemote: false,
    createdAt: '2026-07-01T10:00:00Z', createdBy: 'alice', changeNote: 'tuned threshold',
  },
  {
    version: 1, name: 'Disk Check', description: null, engine: 'auto', runsRemote: false,
    createdAt: '2026-06-30T09:00:00Z', createdBy: 'bob', changeNote: null,
  },
];

/** Registers the list GET (the only query the page issues eagerly). */
function seed(rows: ReturnType<typeof entry>[] = [entry()]) {
  server.use(
    http.get(`${BASE}/api/custom-activities`, () => HttpResponse.json(rows)),
  );
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(
    <QueryClientProvider client={qc}>
      <CustomActivitiesPage />
    </QueryClientProvider>,
  );
  return { ...view, queryClient: qc };
}

describe('CustomActivitiesPage', () => {
  it('rendersList_withStatusScopeAndVersion', async () => {
    seed([
      entry({ id: 'a', key: 'disk-check', name: 'Disk Check', isEnabled: true, version: 3 }),
      entry({ id: 'b', key: 'dns-flush', name: 'DNS Flush', isEnabled: false, runsRemote: true, version: 1 }),
    ]);
    renderPage();

    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());
    expect(screen.getByText('DNS Flush')).toBeInTheDocument();
    expect(screen.getByText('disk-check')).toBeInTheDocument();
    expect(screen.getByText('Live')).toBeInTheDocument();
    expect(screen.getByText('Draft')).toBeInTheDocument();
    expect(screen.getByText('v3')).toBeInTheDocument();
  });

  it('viewerRole_hidesImportExportAndVersionsActions', async () => {
    seed([entry({ name: 'RO Node' })]);
    renderPage('Viewer');
    await waitFor(() => expect(screen.getByText('RO Node')).toBeInTheDocument());
    expect(screen.queryByText('Import')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Version history')).not.toBeInTheDocument();
  });

  it('versionsButton_opensModal_listingSnapshots', async () => {
    seed([entry()]);
    server.use(http.get(`${BASE}/api/custom-activities/ca-1/versions`, () => HttpResponse.json(VERSIONS)));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Version history'));
    await waitFor(() => expect(screen.getByRole('heading', { name: /Version history/i })).toBeInTheDocument());

    // Current live version in the header; the two snapshots as rows with metadata.
    expect(screen.getByText(/Current state: v3/i)).toBeInTheDocument();
    expect(await screen.findByText('v2')).toBeInTheDocument();
    expect(screen.getByText('v1')).toBeInTheDocument();
    expect(screen.getByText('tuned threshold')).toBeInTheDocument();
    expect(screen.getByText('alice')).toBeInTheDocument();
    // Both non-current versions offer a rollback.
    expect(screen.getAllByTitle('Roll back to this version')).toHaveLength(2);
  });

  it('versionsModal_emptyHistory_showsEmptyMessage', async () => {
    seed([entry()]);
    server.use(http.get(`${BASE}/api/custom-activities/ca-1/versions`, () => HttpResponse.json([])));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Version history'));
    await waitFor(() => expect(screen.getByText(/No previous versions yet/i)).toBeInTheDocument());
  });

  it('rollback_confirmsThenPosts_andToasts', async () => {
    let postedUrl: string | null = null;
    seed([entry()]);
    server.use(
      http.get(`${BASE}/api/custom-activities/ca-1/versions`, () => HttpResponse.json(VERSIONS)),
      http.post(`${BASE}/api/custom-activities/ca-1/rollback/2`, ({ request }) => {
        postedUrl = request.url;
        return HttpResponse.json({});
      }),
    );
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Version history'));
    await waitFor(() => expect(screen.getAllByTitle('Roll back to this version')).toHaveLength(2));

    fireEvent.click(screen.getAllByTitle('Roll back to this version')[0]); // newest snapshot = v2
    expect(confirmDialog).toHaveBeenCalledWith(
      expect.objectContaining({ message: expect.stringContaining('version 2'), danger: true }),
    );
    await waitFor(() => expect(postedUrl).toContain('/api/custom-activities/ca-1/rollback/2'));
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((x) => x.kind === 'success' && x.message.includes('version 2')),
    ).toBe(true));
  });

  it('rollback_whenConfirmCancelled_doesNotPost', async () => {
    let posted = false;
    seed([entry()]);
    server.use(
      http.get(`${BASE}/api/custom-activities/ca-1/versions`, () => HttpResponse.json(VERSIONS)),
      http.post(`${BASE}/api/custom-activities/ca-1/rollback/2`, () => {
        posted = true;
        return HttpResponse.json({});
      }),
    );
    vi.mocked(confirmDialog).mockResolvedValue(false);
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Version history'));
    await waitFor(() => expect(screen.getAllByTitle('Roll back to this version')).toHaveLength(2));
    fireEvent.click(screen.getAllByTitle('Roll back to this version')[0]);

    await new Promise((r) => setTimeout(r, 50));
    expect(posted).toBe(false);
  });

  it('enabledDefinition_operatorSeesNoRollbackButtons', async () => {
    // Enabled (live) definitions are Admin-only mutable — the modal mirrors the server gate.
    seed([entry({ isEnabled: true })]);
    server.use(http.get(`${BASE}/api/custom-activities/ca-1/versions`, () => HttpResponse.json(VERSIONS)));
    renderPage('Operator');
    await waitFor(() => expect(screen.getByText('Disk Check')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle('Version history'));
    await waitFor(() => expect(screen.getByText('v2')).toBeInTheDocument());
    expect(screen.queryByTitle('Roll back to this version')).not.toBeInTheDocument();
  });

  it('import_viaFileInput_postsParsedEnvelope_andToasts', async () => {
    let postedBody: Record<string, unknown> | null = null;
    seed([]);
    server.use(http.post(`${BASE}/api/custom-activities/import`, async ({ request }) => {
      postedBody = (await request.json()) as Record<string, unknown>;
      return HttpResponse.json([{}, {}]); // two imported definitions
    }));
    const { container } = renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No custom nodes yet/i)).toBeInTheDocument());

    const envelope = {
      schema: 'nodepilot-customactivity-export/v1',
      exportVersion: 1,
      exportedAt: '2026-07-04T00:00:00Z',
      items: [{ key: 'imported-node', name: 'Imported Node' }],
    };
    const fileInput = container.querySelector<HTMLInputElement>('input[type=file]')!;
    const file = new File([JSON.stringify(envelope)], 'custom-nodes.npca', { type: 'application/json' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    await waitFor(() => expect(postedBody).not.toBeNull());
    expect(postedBody).toMatchObject({ schema: 'nodepilot-customactivity-export/v1' });
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((x) => x.kind === 'success' && x.message.includes('2')),
    ).toBe(true));
  });

  it('import_invalidJson_toastsError_withoutPosting', async () => {
    let posted = false;
    seed([]);
    server.use(http.post(`${BASE}/api/custom-activities/import`, () => {
      posted = true;
      return HttpResponse.json([]);
    }));
    const { container } = renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No custom nodes yet/i)).toBeInTheDocument());

    const fileInput = container.querySelector<HTMLInputElement>('input[type=file]')!;
    const file = new File(['{not json'], 'broken.npca', { type: 'application/json' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    await waitFor(() => expect(
      useToastStore.getState().toasts.some((x) => x.kind === 'error' && x.message.includes('broken.npca')),
    ).toBe(true));
    expect(posted).toBe(false);
  });

  it('does not download an export whose response crosses an auth boundary', async () => {
    let releaseExport!: () => void;
    const exportGate = new Promise<void>((resolve) => { releaseExport = resolve; });
    let requestStarted = false;
    seed([]);
    server.use(http.get(`${BASE}/api/custom-activities/export`, async () => {
      requestStarted = true;
      await exportGate;
      return HttpResponse.json({ items: [{ name: 'User A private node' }] });
    }));
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No custom nodes yet/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^Export$/i }));
    await waitFor(() => expect(requestStarted).toBe(true));
    clearLocalAuthBoundary();
    releaseExport();

    await new Promise((resolve) => globalThis.setTimeout(resolve, 25));
    expect(anchorClick).not.toHaveBeenCalled();
  });

  it('does not post or toast when a file read fails after the auth boundary changed', async () => {
    let rejectRead!: (error: Error) => void;
    const deferredRead = new Promise<string>((_resolve, reject) => { rejectRead = reject; });
    let posted = false;
    seed([]);
    server.use(http.post(`${BASE}/api/custom-activities/import`, () => {
      posted = true;
      return HttpResponse.json([]);
    }));
    const { container, queryClient } = renderPage('Admin');
    await waitFor(() => expect(screen.getByText(/No custom nodes yet/i)).toBeInTheDocument());

    const file = new File(['old-user'], 'user-a-private.npca', { type: 'application/json' });
    const readFile = vi.spyOn(file, 'text').mockReturnValue(deferredRead);
    const fileInput = container.querySelector<HTMLInputElement>('input[type=file]')!;
    fireEvent.change(fileInput, { target: { files: [file] } });
    await waitFor(() => expect(readFile).toHaveBeenCalledTimes(1));

    clearLocalAuthBoundary();
    rejectRead(new Error('Could not read user-a-private.npca'));

    await waitFor(() => expect(
      queryClient.getMutationCache().getAll().some((mutation) => mutation.state.status === 'error'),
    ).toBe(true));
    expect(posted).toBe(false);
    expect(useToastStore.getState().toasts).toEqual([]);
  });

  it('createDialog_codeMirrorEdit_updatesScriptTemplateState', async () => {
    let postedBody: Record<string, unknown> | null = null;
    seed([]);
    server.use(http.post(`${BASE}/api/custom-activities`, async ({ request }) => {
      postedBody = (await request.json()) as Record<string, unknown>;
      return HttpResponse.json({ definition: entry(), warnings: [] }, { status: 201 });
    }));
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Custom Node/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Custom Node/i }));

    const dialog = screen.getByRole('heading', { name: /Create custom node/i }).parentElement as HTMLElement;
    const createBtn = () => within(dialog).getByRole('button', { name: 'Create' });

    fireEvent.change(within(dialog).getAllByRole('textbox')[0], { target: { value: 'My Node' } });
    fireEvent.change(within(dialog).getByPlaceholderText('disk-check'), { target: { value: 'my-node' } });
    expect(createBtn()).toBeDisabled(); // script template still empty

    // CodeMirror renders a contenteditable .cm-content and syncs DOM mutations back into
    // its state via MutationObserver — mutate the DOM directly to simulate typing.
    const cmContent = dialog.querySelector('.cm-content') as HTMLElement;
    expect(cmContent).not.toBeNull();
    cmContent.textContent = 'Get-PSDrive C';

    // onChange must have propagated into form state: the save gate opens.
    await waitFor(() => expect(createBtn()).toBeEnabled());

    fireEvent.click(createBtn());
    await waitFor(() => expect(postedBody).not.toBeNull());
    expect(postedBody).toMatchObject({ key: 'my-node', name: 'My Node', scriptTemplate: 'Get-PSDrive C' });
  });

  it('createDialog_fieldsUseThePageDialogOutlineStyle_notTheRecessedInputField', async () => {
    // `.input-field` is the recessed designer-panel style, which needs a raised surface to
    // sink into. ModalShell only provides that lift in the dark skins, so `light-grey`
    // dialogs need the outline chain every other admin dialog uses. This pins the class
    // choice so a copy-paste from a designer panel does not silently swap it back.
    seed([]);
    renderPage('Admin');
    await waitFor(() => expect(screen.getByRole('button', { name: /New Custom Node/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Custom Node/i }));

    const dialog = screen.getByRole('heading', { name: /Create custom node/i }).parentElement as HTMLElement;
    const fields = [...dialog.querySelectorAll('input:not([type="checkbox"]):not([type="color"]), select')];
    expect(fields.length).toBeGreaterThan(0);
    for (const field of fields) {
      expect(field.className).not.toContain('input-field');
      expect(field.className).toContain('border-outline-variant');
    }

    // The ParamEditor rows must keep their explicit widths — under `.input-field` the unlayered
    // `width:100%` beat them and every row wrapped onto a line of its own.
    fireEvent.click(within(dialog).getByRole('button', { name: /add input/i }));
    const paramName = await waitFor(() => within(dialog).getByPlaceholderText(/^name$/i));
    expect(paramName.className).toContain('w-28');
    expect(paramName.className).not.toContain('w-full');
  });

  it('defaultSort_isNameAsc', async () => {
    seed([
      entry({ id: 'a', key: 'zeta', name: 'Zeta', version: 1 }),
      entry({ id: 'b', key: 'alpha', name: 'Alpha', version: 10 }),
      entry({ id: 'c', key: 'mid', name: 'Mid', version: 2 }),
    ]);
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Zeta')).toBeInTheDocument());
    const names = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-medium')?.textContent?.trim());
    expect(names()).toEqual(['Alpha', 'Mid', 'Zeta']);
  });

  it('clickNameSort_togglesAscDesc', async () => {
    seed([
      entry({ id: 'a', key: 'c', name: 'Charlie' }),
      entry({ id: 'b', key: 'a', name: 'Alpha' }),
      entry({ id: 'c', key: 'b', name: 'Bravo' }),
    ]);
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Bravo')).toBeInTheDocument());
    const names = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td .text-sm.font-medium')?.textContent?.trim());
    expect(names()).toEqual(['Alpha', 'Bravo', 'Charlie']); // default asc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(names()).toEqual(['Charlie', 'Bravo', 'Alpha']); // toggled to desc
    fireEvent.click(screen.getByRole('button', { name: /^Name$/ }));
    expect(names()).toEqual(['Alpha', 'Bravo', 'Charlie']); // back to asc
  });

  it('sortByVersion_numericNotLexical', async () => {
    seed([
      entry({ id: 'a', key: 'zeta', name: 'Zeta', version: 1 }),
      entry({ id: 'b', key: 'alpha', name: 'Alpha', version: 10 }),
      entry({ id: 'c', key: 'mid', name: 'Mid', version: 2 }),
    ]);
    const { container } = renderPage();
    await waitFor(() => expect(screen.getByText('Zeta')).toBeInTheDocument());
    const versions = () => Array.from(container.querySelectorAll('tbody tr'))
      .map((tr) => tr.querySelector('td.tabular-nums')?.textContent?.trim());
    fireEvent.click(screen.getByRole('button', { name: /^Version$/ }));
    expect(versions()).toEqual(['v1', 'v2', 'v10']); // numeric asc (not lexical v1,v10,v2)
    fireEvent.click(screen.getByRole('button', { name: /^Version$/ }));
    expect(versions()).toEqual(['v10', 'v2', 'v1']); // numeric desc
  });
});
