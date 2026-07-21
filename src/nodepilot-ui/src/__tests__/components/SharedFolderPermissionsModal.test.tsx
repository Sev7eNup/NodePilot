import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SharedFolderPermissionsModal } from '../../components/workflows/SharedFolderPermissionsModal';
import {
  ACTIVE_DIRECTORY_AUTHORITY,
  sharedFoldersApi,
  type SharedFolderPermission,
} from '../../api/sharedFolders';

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

const mockApi = sharedFoldersApi as unknown as {
  listPermissions: ReturnType<typeof vi.fn>;
  grantPermission: ReturnType<typeof vi.fn>;
  updatePermission: ReturnType<typeof vi.fn>;
  revokePermission: ReturnType<typeof vi.fn>;
};

function makePermission(overrides: Partial<SharedFolderPermission> = {}): SharedFolderPermission {
  return {
    id: 'perm-id',
    folderId: 'folder-id',
    principalType: 'User',
    principalKey: 'user-id',
    principalDisplayName: 'alice',
    role: 'FolderViewer',
    grantedAt: new Date().toISOString(),
    grantedByUserId: null,
    ...overrides,
  };
}

const users = [
  { id: 'user-1', username: 'alice' },
  { id: 'user-2', username: 'bob' },
  { id: 'user-3', username: 'carol' },
];

describe('SharedFolderPermissionsModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('lists existing permissions on open', async () => {
    mockApi.listPermissions.mockResolvedValue([
      makePermission({ id: 'p1', principalKey: 'user-1', principalDisplayName: 'alice', role: 'FolderViewer' }),
      makePermission({ id: 'p2', principalKey: 'user-2', principalDisplayName: 'bob', role: 'FolderEditor' }),
    ]);

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    // Two grants → two rows in the existing-permissions table.
    await waitFor(() =>
      expect(screen.queryAllByText(/alice/).length).toBeGreaterThanOrEqual(1),
    );
    expect(screen.queryAllByText(/bob/).length).toBeGreaterThanOrEqual(1);
    // Row + select narrative is fine; both names appear in the table cells.
  });

  it('only shows users without grants in the picker', async () => {
    // alice already has a grant — she should NOT be in the unassigned-picker dropdown.
    mockApi.listPermissions.mockResolvedValue([
      makePermission({ id: 'p1', principalKey: 'user-1', principalDisplayName: 'alice' }),
    ]);

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());
    const picker = screen.getByTestId('shared-folder-perms-user-picker') as HTMLSelectElement;
    const optionTexts = Array.from(picker.querySelectorAll('option')).map((o) => o.textContent);
    expect(optionTexts).toContain('bob');
    expect(optionTexts).toContain('carol');
    expect(optionTexts).not.toContain('alice');
  });

  it('grants a permission and reloads', async () => {
    mockApi.listPermissions.mockResolvedValueOnce([]);
    mockApi.listPermissions.mockResolvedValue([
      makePermission({ id: 'p-new', principalKey: 'user-2', principalDisplayName: 'bob', role: 'FolderEditor' }),
    ]);
    mockApi.grantPermission.mockResolvedValue({});

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('Keine expliziten Berechtigungen.')).toBeInTheDocument());

    await userEvent.selectOptions(
      screen.getByTestId('shared-folder-perms-user-picker'),
      'user-2',
    );
    await userEvent.selectOptions(
      screen.getByTestId('shared-folder-perms-role-picker'),
      'FolderEditor',
    );
    await userEvent.click(screen.getByTestId('shared-folder-perms-grant-btn'));

    await waitFor(() =>
      expect(mockApi.grantPermission).toHaveBeenCalledWith(
        'folder-id', 'User', 'user-2', 'FolderEditor', undefined,
      ),
    );
  });

  it('grants a directory group by stable SID', async () => {
    mockApi.listPermissions.mockResolvedValue([]);
    mockApi.grantPermission.mockResolvedValue({});

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('Keine expliziten Berechtigungen.')).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByTestId('shared-folder-perms-principal-type'), 'Group');
    await userEvent.type(screen.getByTestId('shared-folder-perms-group-key'), 'S-1-5-21-1-2-3-4001');
    await userEvent.selectOptions(screen.getByTestId('shared-folder-perms-role-picker'), 'FolderOperator');
    await userEvent.click(screen.getByTestId('shared-folder-perms-grant-btn'));

    await waitFor(() =>
      expect(mockApi.grantPermission).toHaveBeenCalledWith(
        'folder-id', 'Group', 'S-1-5-21-1-2-3-4001', 'FolderOperator',
        ACTIVE_DIRECTORY_AUTHORITY,
      ),
    );
  });

  it('grants an OIDC group with its exact issuer namespace', async () => {
    mockApi.listPermissions.mockResolvedValue([]);
    mockApi.grantPermission.mockResolvedValue({});

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('Keine expliziten Berechtigungen.')).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByTestId('shared-folder-perms-principal-type'), 'Group');
    await userEvent.selectOptions(screen.getByTestId('shared-folder-perms-group-authority-mode'), 'oidc');
    await userEvent.type(
      screen.getByTestId('shared-folder-perms-group-authority'),
      'https://login.example.test/tenant/v2.0',
    );
    await userEvent.type(screen.getByTestId('shared-folder-perms-group-key'), 'finance-team');
    await userEvent.click(screen.getByTestId('shared-folder-perms-grant-btn'));

    await waitFor(() => expect(mockApi.grantPermission).toHaveBeenCalledWith(
      'folder-id', 'Group', 'finance-team', 'FolderViewer',
      'https://login.example.test/tenant/v2.0',
    ));
  });

  it('changes role via dropdown updates server-side', async () => {
    mockApi.listPermissions.mockResolvedValue([
      makePermission({ id: 'p1', principalDisplayName: 'alice', role: 'FolderViewer' }),
    ]);
    mockApi.updatePermission.mockResolvedValue(undefined);

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());
    const roleSelect = screen.getAllByRole('combobox')[0];
    await userEvent.selectOptions(roleSelect, 'FolderOperator');

    await waitFor(() =>
      expect(mockApi.updatePermission).toHaveBeenCalledWith('folder-id', 'p1', 'FolderOperator'),
    );
  });

  it('revoke confirms before calling API', async () => {
    mockApi.listPermissions.mockResolvedValue([
      makePermission({ id: 'p1', principalDisplayName: 'alice' }),
    ]);
    mockApi.revokePermission.mockResolvedValue(undefined);

    render(
      <SharedFolderPermissionsModal
        folderId="folder-id"
        folderPath="/Finance"
        users={users}
        onClose={() => {}}
      />,
    );

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());
    await userEvent.click(screen.getByText('Entfernen'));

    await waitFor(() =>
      expect(mockApi.revokePermission).toHaveBeenCalledWith('folder-id', 'p1'),
    );
    expect(confirmDialog).toHaveBeenCalled();
  });
});
