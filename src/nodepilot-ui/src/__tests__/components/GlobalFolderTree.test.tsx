import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import { GlobalFolderTree } from '../../components/globals/GlobalFolderTree';
import { globalFoldersApi, ROOT_FOLDER_ID, type GlobalFolder } from '../../api/globalFolders';

/**
 * Tests for the global-variable folder tree: one checkbox per row, one request per top-most
 * folder, recursive delete. Access is gated on a single `canManage` flag instead of per-folder
 * capabilities, because global variables carry no folder RBAC.
 */
function renderWithClient(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

vi.mock('../../api/globalFolders', async () => {
  const actual = await vi.importActual<typeof import('../../api/globalFolders')>('../../api/globalFolders');
  return {
    ...actual,
    globalFoldersApi: {
      list: vi.fn(),
      create: vi.fn(),
      rename: vi.fn(),
      move: vi.fn(),
      delete: vi.fn(),
      deleteRecursive: vi.fn(),
      moveVariableToFolder: vi.fn(),
    },
  };
});

vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';
import { useToastStore } from '../../stores/toastStore';

const mockApi = globalFoldersApi as unknown as {
  list: ReturnType<typeof vi.fn>;
  delete: ReturnType<typeof vi.fn>;
  deleteRecursive: ReturnType<typeof vi.fn>;
};

function makeFolder(overrides: Partial<GlobalFolder>): GlobalFolder {
  return {
    id: 'folder-id',
    parentFolderId: null,
    name: 'Folder',
    path: '/Folder',
    depth: 1,
    createdAt: '2026-01-01T00:00:00Z',
    createdByUserId: null,
    variableCount: 0,
    ...overrides,
  };
}

const rootFolder = makeFolder({ id: ROOT_FOLDER_ID, name: 'Root', path: '/', depth: 0 });
const env = makeFolder({ id: 'env', name: 'Environment', path: '/Environment', parentFolderId: ROOT_FOLDER_ID, variableCount: 2 });
const prod = makeFolder({ id: 'prod', name: 'Prod', path: '/Environment/Prod', parentFolderId: 'env', depth: 2, variableCount: 3 });
const keys = makeFolder({ id: 'keys', name: 'Keys', path: '/Keys', parentFolderId: ROOT_FOLDER_ID, variableCount: 1 });

describe('GlobalFolderTree', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(confirmDialog).mockResolvedValue(true);
    useToastStore.setState({ toasts: [] });
  });

  it('shows no bulk controls without canManage', async () => {
    mockApi.list.mockResolvedValue([rootFolder, env]);

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage={false} />);
    await waitFor(() => expect(screen.getByText('Environment')).toBeInTheDocument());

    expect(screen.queryByTestId('global-folder-select-env')).not.toBeInTheDocument();
    expect(screen.queryByTestId('folder-bulk-bar')).not.toBeInTheDocument();
  });

  it('gives every row but Root a checkbox', async () => {
    // Root cannot be deleted, so offering to select it would only produce a failing request.
    mockApi.list.mockResolvedValue([rootFolder, env]);

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage />);
    await waitFor(() => expect(screen.getByText('Environment')).toBeInTheDocument());

    expect(screen.getByTestId('global-folder-select-env')).toBeInTheDocument();
    expect(screen.queryByTestId(`global-folder-select-${ROOT_FOLDER_ID}`)).not.toBeInTheDocument();
  });

  it('selecting a folder does not also change the folder filter', async () => {
    // A row click means "filter to this folder"; a checkbox click must not do both.
    mockApi.list.mockResolvedValue([rootFolder, env]);
    const onFolderSelected = vi.fn();

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={onFolderSelected} canManage />);
    await waitFor(() => expect(screen.getByText('Environment')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('global-folder-select-env'));

    expect(onFolderSelected).not.toHaveBeenCalled();
    expect(screen.getByTestId('folder-bulk-bar')).toBeInTheDocument();
  });

  it('deletes one request per top-most folder, not per selected folder', async () => {
    // Parent + child selected: the child is inside the parent's subtree, so a second request
    // would find nothing and 404.
    mockApi.list.mockResolvedValue([rootFolder, env, prod]);
    mockApi.deleteRecursive.mockResolvedValue({ deletedFolders: 2, deletedVariables: 5 });

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage />);
    await waitFor(() => expect(screen.getByText('Prod')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('global-folder-select-env'));
    await userEvent.click(screen.getByTestId('global-folder-select-prod'));
    await userEvent.click(screen.getByTestId('folder-bulk-delete'));

    await waitFor(() => expect(mockApi.deleteRecursive).toHaveBeenCalledTimes(1));
    expect(mockApi.deleteRecursive).toHaveBeenCalledWith('env');
    // One confirmation covers the whole run.
    expect(confirmDialog).toHaveBeenCalledTimes(1);
  });

  it('sends one request per selected sibling', async () => {
    mockApi.list.mockResolvedValue([rootFolder, env, keys]);
    mockApi.deleteRecursive.mockResolvedValue({ deletedFolders: 1, deletedVariables: 1 });

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage />);
    await waitFor(() => expect(screen.getByText('Keys')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('global-folder-select-env'));
    await userEvent.click(screen.getByTestId('global-folder-select-keys'));
    await userEvent.click(screen.getByTestId('folder-bulk-delete'));

    await waitFor(() => expect(mockApi.deleteRecursive).toHaveBeenCalledTimes(2));
  });

  it('the confirmation names what goes with the folder', async () => {
    // A recursive delete is only safe if the dialog states what the folder takes with it.
    mockApi.list.mockResolvedValue([rootFolder, env, prod]);
    mockApi.deleteRecursive.mockResolvedValue({ deletedFolders: 2, deletedVariables: 5 });

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage />);
    await waitFor(() => expect(screen.getByText('Environment')).toBeInTheDocument());

    await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Environment') });
    await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));

    await waitFor(() => expect(confirmDialog).toHaveBeenCalled());
    const request = vi.mocked(confirmDialog).mock.calls[0][0] as { details?: string[]; danger?: boolean };
    expect(request.danger).toBe(true);
    expect(request.details?.join(' ')).toContain('/Environment');
  });

  it('context-menu delete is recursive and never falls back to the empty-only call', async () => {
    mockApi.list.mockResolvedValue([rootFolder, env]);
    mockApi.deleteRecursive.mockResolvedValue({ deletedFolders: 1, deletedVariables: 2 });

    renderWithClient(
      <GlobalFolderTree selectedFolderId={null} onFolderSelected={() => {}} canManage />);
    await waitFor(() => expect(screen.getByText('Environment')).toBeInTheDocument());

    // First: the user cancels.
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Environment') });
    await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));
    await waitFor(() => expect(confirmDialog).toHaveBeenCalled());
    expect(mockApi.deleteRecursive).not.toHaveBeenCalled();

    // Then: the user confirms.
    await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByText('Environment') });
    await userEvent.click(screen.getByTestId('shared-folder-menu-delete'));
    await waitFor(() => expect(mockApi.deleteRecursive).toHaveBeenCalledWith('env'));
    expect(mockApi.delete).not.toHaveBeenCalled();
  });

  it('resets the folder filter when the filtered folder is a descendant of a deleted one', async () => {
    // /Environment/Prod is never requested; it disappears with its parent.
    mockApi.list.mockResolvedValue([rootFolder, env, prod]);
    mockApi.deleteRecursive.mockResolvedValue({ deletedFolders: 2, deletedVariables: 5 });
    const onFolderSelected = vi.fn();

    renderWithClient(
      <GlobalFolderTree selectedFolderId="prod" onFolderSelected={onFolderSelected} canManage />);
    await waitFor(() => expect(screen.getByText('Prod')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('global-folder-select-env'));
    await userEvent.click(screen.getByTestId('folder-bulk-delete'));

    await waitFor(() => expect(onFolderSelected).toHaveBeenCalledWith(ROOT_FOLDER_ID));
  });

  it('keeps the folder filter when the delete failed', async () => {
    // The folder still exists after a failed delete, so the filter must stay on it.
    mockApi.list.mockResolvedValue([rootFolder, env, prod]);
    mockApi.deleteRecursive.mockRejectedValue(new Error('nope'));
    const onFolderSelected = vi.fn();

    renderWithClient(
      <GlobalFolderTree selectedFolderId="prod" onFolderSelected={onFolderSelected} canManage />);
    await waitFor(() => expect(screen.getByText('Prod')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('global-folder-select-env'));
    await userEvent.click(screen.getByTestId('folder-bulk-delete'));

    await waitFor(() => expect(useToastStore.getState().toasts.length).toBeGreaterThan(0));
    expect(onFolderSelected).not.toHaveBeenCalled();
  });
});
