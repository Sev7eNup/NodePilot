import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import { SharedFolderTree } from '../../components/workflows/SharedFolderTree';
import { sharedFoldersApi, ROOT_FOLDER_ID, type SharedFolder } from '../../api/sharedFolders';

/**
 * Tree now reads from a shared react-query cache (queryKey: ['shared-folders']) so
 * mutations elsewhere (workflow create, move-folder, etc.) flow through invalidation
 * and the tree's workflowCount badges update reactively. Each test gets a fresh
 * QueryClient with retries off so a 1st-call mock failure doesn't trigger an
 * exponential-backoff retry loop in the test runtime.
 */
function renderWithClient(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

vi.mock('../../api/sharedFolders', async () => {
  const actual = await vi.importActual<typeof import('../../api/sharedFolders')>('../../api/sharedFolders');
  return {
    ...actual,
    sharedFoldersApi: {
      list: vi.fn(),
      create: vi.fn(),
      rename: vi.fn(),
      move: vi.fn(),
      delete: vi.fn(),
      moveWorkflowToFolder: vi.fn(),
      listPermissions: vi.fn(),
      grantPermission: vi.fn(),
      updatePermission: vi.fn(),
      revokePermission: vi.fn(),
    },
  };
});

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';
import { useToastStore } from '../../stores/toastStore';

const mockApi = sharedFoldersApi as unknown as {
  list: ReturnType<typeof vi.fn>;
  create: ReturnType<typeof vi.fn>;
  rename: ReturnType<typeof vi.fn>;
  delete: ReturnType<typeof vi.fn>;
};

function makeFolder(overrides: Partial<SharedFolder>): SharedFolder {
  return {
    id: 'folder-id',
    parentFolderId: null,
    name: 'Folder',
    path: '/Folder',
    depth: 1,
    createdAt: new Date().toISOString(),
    createdByUserId: null,
    workflowCount: 0,
    capabilities: { canRead: true, canRun: false, canEdit: false, canAdmin: false },
    ...overrides,
  };
}

describe('SharedFolderTree', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // clearAllMocks wipes call history but keeps the factory's resolved-true default.
    useToastStore.setState({ toasts: [] });
  });

  it('renders Root + nested children with workflow counts', async () => {
    mockApi.list.mockResolvedValue([
      makeFolder({ id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0, workflowCount: 5 }),
      makeFolder({ id: 'finance', parentFolderId: ROOT_FOLDER_ID, name: 'Finance', path: '/Finance', depth: 1, workflowCount: 3 }),
      makeFolder({ id: 'reports', parentFolderId: 'finance', name: 'Reports', path: '/Finance/Reports', depth: 2, workflowCount: 1 }),
    ]);

    renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

    await waitFor(() => expect(screen.getByText(/\\/)).toBeInTheDocument());
    expect(screen.getByText('Finance')).toBeInTheDocument();
    expect(screen.getByText('Reports')).toBeInTheDocument();
    expect(screen.getByText('5')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('emits onFolderSelected when a folder is clicked', async () => {
    mockApi.list.mockResolvedValue([
      makeFolder({ id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0 }),
      makeFolder({ id: 'finance', parentFolderId: ROOT_FOLDER_ID, name: 'Finance', path: '/Finance', depth: 1 }),
    ]);
    const onSelect = vi.fn();

    renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={onSelect} />);

    await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());
    await userEvent.click(screen.getByText('Finance'));
    expect(onSelect).toHaveBeenCalledWith('finance');
  });

  it('hides + button when caller has no canEdit', async () => {
    mockApi.list.mockResolvedValue([
      makeFolder({
        id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0,
        capabilities: { canRead: true, canRun: false, canEdit: false, canAdmin: false },
      }),
    ]);

    renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

    await waitFor(() => expect(screen.getByText(/\\/)).toBeInTheDocument());
    // The "+" button is rendered only when canEdit is true.
    expect(screen.queryByTitle('Create subfolder')).not.toBeInTheDocument();
  });

  it('shows + button and creates a sub-folder when capabilities allow', async () => {
    const created = vi.fn();
    mockApi.list.mockResolvedValueOnce([
      makeFolder({
        id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0,
        capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
      }),
    ]);
    mockApi.list.mockResolvedValue([
      makeFolder({ id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0,
        capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true } }),
      makeFolder({ id: 'new', parentFolderId: ROOT_FOLDER_ID, name: 'NewFolder', path: '/NewFolder', depth: 1 }),
    ]);
    mockApi.create.mockResolvedValue({});

    renderWithClient(
      <SharedFolderTree
        selectedFolderId={null}
        onFolderSelected={() => {}}
        onTreeMutated={created}
      />,
    );

    await waitFor(() => expect(screen.getByText(/\\/)).toBeInTheDocument());
    const plusBtn = screen.getByTitle('Create subfolder');
    await userEvent.click(plusBtn);
    const input = screen.getByTestId('shared-folder-create-input');
    await userEvent.type(input, 'NewFolder');
    await userEvent.keyboard('{Enter}');

    await waitFor(() => expect(mockApi.create).toHaveBeenCalledWith(ROOT_FOLDER_ID, 'NewFolder'));
    await waitFor(() => expect(created).toHaveBeenCalled());
  });

  describe('context menu', () => {
    const editableFolder = makeFolder({
      id: 'finance', parentFolderId: ROOT_FOLDER_ID, name: 'Finance', path: '/Finance', depth: 1,
      capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    });
    const readOnlyFolder = makeFolder({
      id: 'reports', parentFolderId: ROOT_FOLDER_ID, name: 'Reports', path: '/Reports', depth: 1,
      capabilities: { canRead: true, canRun: false, canEdit: false, canAdmin: false },
    });
    const rootFolder = makeFolder({
      id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0,
      capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    });

    it('opens menu on right-click of an editable non-root folder', async () => {
      mockApi.list.mockResolvedValue([rootFolder, editableFolder]);
      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

      await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });

      expect(screen.getByTestId('shared-folder-context-menu')).toBeInTheDocument();
      expect(screen.getByTestId('shared-folder-menu-rename')).toBeInTheDocument();
      expect(screen.getByTestId('shared-folder-menu-delete')).toBeInTheDocument();
    });

    it('does NOT open menu on right-click of root folder', async () => {
      mockApi.list.mockResolvedValue([rootFolder]);
      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

      await waitFor(() => expect(screen.getByText(/\\/)).toBeInTheDocument());
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText(/\\/) });

      expect(screen.queryByTestId('shared-folder-context-menu')).not.toBeInTheDocument();
    });

    it('does NOT open menu on right-click of a read-only folder', async () => {
      mockApi.list.mockResolvedValue([rootFolder, readOnlyFolder]);
      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

      await waitFor(() => expect(screen.getByText('Reports')).toBeInTheDocument());
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Reports') });

      expect(screen.queryByTestId('shared-folder-context-menu')).not.toBeInTheDocument();
    });

    it('rename: clicking Umbenennen swaps row for input; Enter calls API and refreshes', async () => {
      // First load returns the original name; after rename, the list returns the renamed
      // value so the cache invalidation actually shows new state.
      mockApi.list.mockResolvedValueOnce([rootFolder, editableFolder]);
      mockApi.list.mockResolvedValue([
        rootFolder,
        { ...editableFolder, name: 'NeuFinance', path: '/NeuFinance' },
      ]);
      mockApi.rename.mockResolvedValue(undefined);

      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);
      await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());

      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });
      await userEvent.click(screen.getByTestId('shared-folder-menu-rename'));

      const input = screen.getByTestId('shared-folder-rename-input');
      expect(input).toHaveValue('Finance');
      await userEvent.clear(input);
      await userEvent.type(input, 'NeuFinance');
      await userEvent.keyboard('{Enter}');

      await waitFor(() => expect(mockApi.rename).toHaveBeenCalledWith('finance', 'NeuFinance'));
    });

    it('rename: Escape cancels without calling API', async () => {
      mockApi.list.mockResolvedValue([rootFolder, editableFolder]);
      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);

      await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });
      await userEvent.click(screen.getByTestId('shared-folder-menu-rename'));

      const input = screen.getByTestId('shared-folder-rename-input');
      await userEvent.clear(input);
      await userEvent.type(input, 'WontSave');
      await userEvent.keyboard('{Escape}');

      expect(screen.queryByTestId('shared-folder-rename-input')).not.toBeInTheDocument();
      expect(mockApi.rename).not.toHaveBeenCalled();
    });

    it('delete: confirm=true calls API; confirm=false does not', async () => {
      mockApi.list.mockResolvedValue([rootFolder, editableFolder]);
      mockApi.delete.mockResolvedValue(undefined);

      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);
      await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());

      // First click: user cancels.
      vi.mocked(confirmDialog).mockResolvedValueOnce(false);
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });
      await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));
      await waitFor(() => expect(confirmDialog).toHaveBeenCalled());
      expect(mockApi.delete).not.toHaveBeenCalled();

      // Second click: user confirms (factory default resolves true).
      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });
      await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));
      await waitFor(() => expect(mockApi.delete).toHaveBeenCalledWith('finance'));
    });

    it('delete: 409 from backend surfaces in an error toast with the backend message', async () => {
      mockApi.list.mockResolvedValue([rootFolder, editableFolder]);
      mockApi.delete.mockRejectedValue(new Error('Folder is not empty — move or delete sub-folders and workflows first'));

      renderWithClient(<SharedFolderTree selectedFolderId={null} onFolderSelected={() => {}} />);
      await waitFor(() => expect(screen.getByText('Finance')).toBeInTheDocument());

      await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Finance') });
      await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));

      await waitFor(() => expect(useToastStore.getState().toasts.length).toBeGreaterThan(0));
      expect(useToastStore.getState().toasts[0].message).toMatch(/not empty/i);
    });
  });
});
