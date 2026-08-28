import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { GlobalVariablesPage } from '../../pages/GlobalVariablesPage';
import { useAuthStore } from '../../stores/authStore';
import { ROOT_FOLDER_ID } from '../../api/globalFolders';

// Store-driven confirm replaces the native confirm(); ConfirmHost is not mounted in this test.
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';

const BASE = 'http://localhost';

// Folder tree fixture: Root plus one topic subfolder. The page filters the list to the selected
// folder's subtree, so variables must carry a folderId that lands in scope (Root shows all).
const FOLDERS = [
  { id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0, createdAt: '2026-04-25T10:00:00Z', createdByUserId: null, variableCount: 2 },
  { id: 'f-db', parentFolderId: ROOT_FOLDER_ID, name: 'Databases', path: '/Databases', depth: 1, createdAt: '2026-04-25T10:00:00Z', createdByUserId: null, variableCount: 1 },
];

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

const VARS = [
  {
    id: 'v1', name: 'API_URL', value: 'https://api.test',
    isSecret: false, description: 'Backend URL', folderId: ROOT_FOLDER_ID,
    createdAt: '2026-04-25T10:00:00Z', updatedAt: '2026-04-25T10:00:00Z',
    updatedBy: 'admin',
  },
  {
    id: 'v2', name: 'API_KEY', value: null,
    isSecret: true, description: null, folderId: 'f-db',
    createdAt: '2026-04-25T10:00:00Z', updatedAt: '2026-04-25T10:00:00Z',
    updatedBy: null,
  },
];

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  // Always mock the folder tree — the page queries it on mount and filters the list by folder.
  server.use(http.get(`${BASE}/api/global-variable-folders`, () => HttpResponse.json(FOLDERS)));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <GlobalVariablesPage />
    </QueryClientProvider>
  );
}

describe('GlobalVariablesPage', () => {
  it('rendersSubtitle', () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage();
    // The page title now lives in the app TopBar; the page keeps the {{globals.NAME}} subtitle.
    expect(screen.getByText('{{globals.NAME}}')).toBeInTheDocument();
  });

  it('emptyVariables_showsEmptyMessage', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage();
    await waitFor(() => expect(screen.getByText(/No global variables yet/)).toBeInTheDocument());
  });

  it('rendersVariableTable_withNamesAndValues', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());
    expect(screen.getByText('API_KEY')).toBeInTheDocument();
    expect(screen.getByText('https://api.test')).toBeInTheDocument();
  });

  it('secretVariable_showsLockBadgeAndMaskedValue', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('API_KEY')).toBeInTheDocument());
    expect(screen.getByText('Secret')).toBeInTheDocument();
    expect(screen.getByText('***')).toBeInTheDocument();
  });

  it('plainVariable_showsUnlockBadge', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('Plain')).toBeInTheDocument());
  });

  it('adminUser_seesNewVariableButton', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText(/No global variables yet/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument();
  });

  it('operatorUser_doesNotSeeNewVariableButton', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Operator');

    await waitFor(() => expect(screen.getByText(/No global variables yet/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New Variable/i })).not.toBeInTheDocument();
  });

  it('viewerUser_doesNotSeeNewVariableButton', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText(/No global variables yet/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /New Variable/i })).not.toBeInTheDocument();
  });

  it('clickNew_opensCreateDialog', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Variable/i }));

    expect(screen.getByRole('heading', { name: /New Variable/i })).toBeInTheDocument();
    expect(screen.getByPlaceholderText('MY_CONSTANT')).toBeInTheDocument();
  });

  it('cancelDialog_closesIt', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Variable/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByPlaceholderText('MY_CONSTANT')).not.toBeInTheDocument();
  });

  it('createButtonDisabled_whenNameEmpty', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Variable/i }));

    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();
  });

  it('toggleSecret_changesValueInputType', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Variable/i }));

    // Find the value input — by default type=text. After toggling secret, type=password.
    // Named explicitly: the folder tree and the list rows carry select checkboxes too.
    const checkbox = screen.getByRole('checkbox', { name: /Secret \(encrypt at rest/i }) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    fireEvent.click(checkbox);
    expect(checkbox.checked).toBe(true);
  });

  it('clickEdit_opensDialogInEditMode', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());
    // Rows render sorted by name asc by default — API_KEY comes before API_URL,
    // so the first Edit button belongs to API_KEY.
    const editButtons = screen.getAllByTitle('Edit');
    fireEvent.click(editButtons[0]);

    expect(screen.getByRole('heading', { name: /Edit Variable/i })).toBeInTheDocument();
    // Name field pre-populated
    expect((screen.getByPlaceholderText('MY_CONSTANT') as HTMLInputElement).value).toBe('API_KEY');
    // Update button (instead of Create) is rendered in edit mode
    expect(screen.getByRole('button', { name: 'Update' })).toBeInTheDocument();
  });

  it('viewerRole_doesNotSeeEditDeleteButtons', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());
    expect(screen.queryByTitle('Edit')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Delete')).not.toBeInTheDocument();
  });

  // ---- Folder organization ----

  it('rendersFolderTree_withSubfolder', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage('Admin');

    expect(await screen.findByTestId('global-folder-tree')).toBeInTheDocument();
    // Wait for the async folder query to hydrate the tree rows.
    expect(await screen.findByText('Databases')).toBeInTheDocument();
  });

  it('selectingSubfolder_scopesListToThatFolder', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage('Admin');

    // Root is selected by default -> descendant-inclusive, so both variables show.
    await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());
    expect(screen.getByText('API_KEY')).toBeInTheDocument();

    // Click the "Databases" subfolder -> list scopes to f-db (only API_KEY lives there).
    fireEvent.click(screen.getByTestId('global-folder-f-db'));

    await waitFor(() => expect(screen.queryByText('API_URL')).not.toBeInTheDocument());
    expect(screen.getByText('API_KEY')).toBeInTheDocument();
  });

  it('createDialog_hasFolderSelect_andPostsFolderId', async () => {
    let posted: Record<string, unknown> | null = null;
    server.use(
      http.get(`${BASE}/api/global-variables`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/global-variables`, async ({ request }) => {
        posted = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ id: 'new' }, { status: 201 });
      }),
    );
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /New Variable/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /New Variable/i }));

    // The dialog carries a folder <select> (the only combobox on the page), defaulting to the
    // currently selected folder (Root).
    expect(screen.getByRole('combobox')).toBeInTheDocument();

    const textboxes = screen.getAllByRole('textbox');
    fireEvent.change(textboxes[0], { target: { value: 'NEW_VAR' } });   // name
    fireEvent.change(textboxes[1], { target: { value: 'a-value' } });   // value
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted!.folderId).toBe(ROOT_FOLDER_ID);
    expect(posted!.name).toBe('NEW_VAR');
  });
  describe('variable multi-select', () => {
    it('selecting rows opens the bulk bar and deletes one by one', async () => {
      // No batch endpoint: each variable keeps its own authorization check and audit row.
      vi.mocked(confirmDialog).mockResolvedValue(true);
      const deleted: string[] = [];
      server.use(
        http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)),
        http.delete(`${BASE}/api/global-variables/:id`, ({ params }) => {
          deleted.push(params.id as string);
          return new HttpResponse(null, { status: 204 });
        }),
      );
      renderPage('Admin');
      await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());

      fireEvent.click(screen.getByTestId('variable-select-v1'));
      fireEvent.click(screen.getByTestId('variable-select-v2'));
      expect(screen.getByTestId('variable-bulk-bar')).toBeInTheDocument();

      fireEvent.click(screen.getByTestId('variable-bulk-delete'));

      await waitFor(() => expect(deleted).toHaveLength(2));
      expect([...deleted].sort()).toEqual(['v1', 'v2']);
      // Names, not just a count — with secrets on the list a bare number is not checkable.
      const request = vi.mocked(confirmDialog).mock.calls[0][0] as { details?: string[]; danger?: boolean };
      expect(request.danger).toBe(true);
      expect(request.details).toEqual(expect.arrayContaining(['API_KEY', 'API_URL']));
    });

    it('the header checkbox takes every visible row, and only visible rows', async () => {
      // Scoped to the selected folder: switching to /Databases must not select the Root variable.
      server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
      renderPage('Admin');
      await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());

      fireEvent.click(screen.getByTestId('global-folder-f-db'));
      await waitFor(() => expect(screen.queryByText('API_URL')).not.toBeInTheDocument());

      fireEvent.click(screen.getByTestId('variable-select-all'));

      expect(screen.getByTestId('variable-bulk-bar').textContent).toContain('1');
    });

    it('a cancelled confirmation deletes nothing', async () => {
      vi.mocked(confirmDialog).mockResolvedValue(false);
      let calls = 0;
      server.use(
        http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)),
        http.delete(`${BASE}/api/global-variables/:id`, () => {
          calls += 1;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      renderPage('Admin');
      await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());

      fireEvent.click(screen.getByTestId('variable-select-v1'));
      fireEvent.click(screen.getByTestId('variable-bulk-delete'));

      await waitFor(() => expect(confirmDialog).toHaveBeenCalled());
      expect(calls).toBe(0);
    });

    it('the bar counts only rows still on screen', async () => {
      // Select in Root, then scope to /Databases: the Root-only variable leaves the list, so the
      // bar must drop it too. Counting the raw id set would leave a button that deletes nothing.
      server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
      renderPage('Admin');
      await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());

      fireEvent.click(screen.getByTestId('variable-select-v1'));   // API_URL, lives in Root
      expect(screen.getByTestId('variable-bulk-bar')).toBeInTheDocument();

      fireEvent.click(screen.getByTestId('global-folder-f-db'));
      await waitFor(() => expect(screen.queryByText('API_URL')).not.toBeInTheDocument());

      await waitFor(() => expect(screen.queryByTestId('variable-bulk-bar')).not.toBeInTheDocument());
    });

    it('a non-admin gets no checkboxes at all', async () => {
      server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
      renderPage('Operator');
      await waitFor(() => expect(screen.getByText('API_URL')).toBeInTheDocument());

      expect(screen.queryByTestId('variable-select-all')).not.toBeInTheDocument();
      expect(screen.queryByTestId('variable-select-v1')).not.toBeInTheDocument();
    });
  });
});
