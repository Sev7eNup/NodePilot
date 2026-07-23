import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route, Navigate } from 'react-router-dom';
import { DbViewerPage } from '../../pages/DbViewerPage';
import { useAuthStore } from '../../stores/authStore';
import { dbAdminApi } from '../../api/dbadmin';
import type { DbAdminTableInfo, DbAdminRowsResponse } from '../../api/dbadmin';

vi.mock('../../api/dbadmin', () => ({
  dbAdminApi: {
    getTables: vi.fn(),
    getRows: vi.fn(),
    patchRow: vi.fn(),
    deleteRow: vi.fn(),
  },
}));

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';

// ─── Helpers ─────────────────────────────────────────────────────────────────

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

// ─── Fixtures ─────────────────────────────────────────────────────────────────

const tableEditable: DbAdminTableInfo = {
  name: 'Workflow',
  displayName: 'Workflows',
  dbTableName: 'Workflows',
  pkColumns: ['Id'],
  capabilities: { canUpdate: true, canDelete: true },
  columns: [
    { name: 'Id',            clrType: 'guid',     isNullable: false, maxLength: null, isPrimaryKey: true,  isMasked: false, isReadOnly: true  },
    { name: 'Name',          clrType: 'string',   isNullable: false, maxLength: 200,  isPrimaryKey: false, isMasked: false, isReadOnly: false },
    { name: 'IsEnabled',     clrType: 'boolean',  isNullable: false, maxLength: null, isPrimaryKey: false, isMasked: false, isReadOnly: false },
    { name: 'CreatedAt',     clrType: 'datetime', isNullable: false, maxLength: null, isPrimaryKey: false, isMasked: false, isReadOnly: false },
    { name: 'Score',         clrType: 'int32',    isNullable: true,  maxLength: null, isPrimaryKey: false, isMasked: false, isReadOnly: false },
    { name: 'DefinitionJson',clrType: 'string',   isNullable: true,  maxLength: null, isPrimaryKey: false, isMasked: false, isReadOnly: false },
    { name: 'MaskedCol',     clrType: 'string',   isNullable: true,  maxLength: 50,   isPrimaryKey: false, isMasked: true,  isReadOnly: false },
    { name: 'ReadonlyCol',   clrType: 'string',   isNullable: true,  maxLength: 100,  isPrimaryKey: false, isMasked: false, isReadOnly: true  },
  ],
  rowCount: 3,
  cascadeDeletesTo: ['WorkflowExecution'],
};

const tableReadOnly: DbAdminTableInfo = {
  name: 'AuditLogEntry',
  displayName: 'Audit Log',
  dbTableName: 'AuditLog',
  pkColumns: ['Id'],
  capabilities: { canUpdate: false, canDelete: false },
  columns: [
    { name: 'Id',     clrType: 'guid',   isNullable: false, maxLength: null, isPrimaryKey: true,  isMasked: false, isReadOnly: false },
    { name: 'Action', clrType: 'string', isNullable: false, maxLength: 100,  isPrimaryKey: false, isMasked: false, isReadOnly: false },
  ],
  rowCount: 1200,
  cascadeDeletesTo: [],
};

const rowsResponse: DbAdminRowsResponse = {
  total: 3,
  rows: [
    {
      Id:            'abc-123',
      Name:          'My Workflow',
      IsEnabled:     true,
      CreatedAt:     '2024-01-15T12:00:00.000Z',
      Score:         42,
      DefinitionJson:'{"nodes":[]}',
      MaskedCol:     '***',
      ReadonlyCol:   'fixed',
    },
  ],
};

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('DbViewerPage', () => {
  beforeEach(() => {
    vi.mocked(dbAdminApi.getTables).mockResolvedValue([tableEditable, tableReadOnly]);
    vi.mocked(dbAdminApi.getRows).mockResolvedValue(rowsResponse);
    vi.mocked(dbAdminApi.patchRow).mockResolvedValue(undefined);
    vi.mocked(dbAdminApi.deleteRow).mockResolvedValue(undefined);
  });

  it('renders_tableList_withRowCounts', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => expect(screen.getByText('Workflows')).toBeInTheDocument());
    expect(screen.getByText('Audit Log')).toBeInTheDocument();
    // Row counts rendered by TableList — use toLocaleString() to match runtime locale
    expect(screen.getByText((3).toLocaleString())).toBeInTheDocument();
    expect(screen.getByText((1200).toLocaleString())).toBeInTheDocument();
  });

  it('readOnly_tables_showLockIcon', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Audit Log'));

    // The Lock icon in TableList.tsx has title="Read-only" (en locale)
    // Only the fully read-only table gets one
    const lockIcons = document.querySelectorAll('[title="Read-only"]');
    expect(lockIcons).toHaveLength(1);
  });

  it('selectTable_triggersRowQuery', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));

    await waitFor(() => {
      expect(dbAdminApi.getRows).toHaveBeenCalledWith(
        'Workflow',
        expect.objectContaining({ skip: 0, take: 100 }),
      );
    });
    await waitFor(() => expect(screen.getByText('My Workflow')).toBeInTheDocument());
  });

  it('tableColumns_canBeResizedWithKeyboardAndPointer', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    const handle = screen.getByRole('separator', { name: 'Resize Name column' });
    const nameColumn = document.querySelectorAll('colgroup col')[1] as HTMLElement;
    expect(nameColumn).toHaveStyle({ width: '200px' });

    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(nameColumn).toHaveStyle({ width: '216px' });

    fireEvent.pointerDown(handle, { clientX: 100 });
    fireEvent.pointerMove(window, { clientX: 150 });
    fireEvent.pointerUp(window);
    expect(nameColumn).toHaveStyle({ width: '266px' });
  });

  it('cellClick_string_opensDialogWithTextInput', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    // Click the Name cell (string, editable)
    await userEvent.click(screen.getByText('My Workflow'));

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());
    expect(document.querySelector('input[type="text"]')).toBeInTheDocument();
  });

  it('cellClick_boolean_opensDialogWithSelect', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    // Click the IsEnabled cell — shows "true"
    const firstRow = document.querySelector('tbody tr')!;
    const cells = firstRow.querySelectorAll('td');
    await userEvent.click(cells[2]); // IsEnabled at index 2

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());
    expect(document.querySelector('select')).toBeInTheDocument();
  });

  it('cellClick_datetime_opensDialogWithDatetimeInput', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    // Click the CreatedAt cell at index 3
    const firstRow = document.querySelector('tbody tr')!;
    const cells = firstRow.querySelectorAll('td');
    await userEvent.click(cells[3]); // CreatedAt

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());
    expect(document.querySelector('input[type="datetime-local"]')).toBeInTheDocument();
  });

  it('cellClick_masked_doesNotOpenDialog', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    // Masked cell shows *** and must not open the edit dialog
    const firstRow = document.querySelector('tbody tr')!;
    const cells = firstRow.querySelectorAll('td');
    await userEvent.click(cells[6]); // MaskedCol at index 6

    expect(screen.queryByText('Edit cell')).not.toBeInTheDocument();
  });

  it('cellClick_readonly_doesNotOpenDialog', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('fixed'));

    // ReadonlyCol cell — no dialog
    await userEvent.click(screen.getByText('fixed'));

    expect(screen.queryByText('Edit cell')).not.toBeInTheDocument();
  });

  it('datetimeEdit_submits_isoUtcString', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    const firstRow = document.querySelector('tbody tr')!;
    await userEvent.click(firstRow.querySelectorAll('td')[3]); // CreatedAt

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());

    const input = document.querySelector('input[type="datetime-local"]') as HTMLInputElement;
    expect(input).not.toBeNull();

    // Simulate picking a local datetime value
    fireEvent.change(input, { target: { value: '2024-03-20T10:00:00' } });
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => {
      expect(dbAdminApi.patchRow).toHaveBeenCalled();
      const [, , , submittedValue] = vi.mocked(dbAdminApi.patchRow).mock.calls[0];
      // Must be ISO UTC — toISOString() with Z suffix, not just appending "Z" to local string
      expect(typeof submittedValue).toBe('string');
      expect(submittedValue as string).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
    });
  });

  it('save_callsPatchRow_andClosesDialog', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    await userEvent.click(screen.getByText('My Workflow'));
    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());

    const input = document.querySelector('input[type="text"]') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'Updated Name' } });
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => {
      expect(dbAdminApi.patchRow).toHaveBeenCalledWith(
        'Workflow',
        ['abc-123'],
        'Name',
        'Updated Name',
      );
    });
    // Dialog closes on success
    await waitFor(() => expect(screen.queryByText('Edit cell')).not.toBeInTheDocument());
  });

  it('numericEdit_emptyOnNullable_submitsNull', async () => {
    // Regression test: Number('') === 0 (not NaN), so clearing a nullable numeric cell used to
    // save 0 instead of null. Fixed in EditCellDialog by forcing an empty string to null before
    // the Number() conversion whenever the column is nullable.
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    // Click the Score cell (int32, nullable, value 42)
    const firstRow = document.querySelector('tbody tr')!;
    const cells = firstRow.querySelectorAll('td');
    await userEvent.click(cells[4]); // Score at index 4

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());
    const input = document.querySelector('input[type="number"]') as HTMLInputElement;
    expect(input).not.toBeNull();

    fireEvent.change(input, { target: { value: '' } });
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => {
      expect(dbAdminApi.patchRow).toHaveBeenCalledWith(
        'Workflow',
        ['abc-123'],
        'Score',
        null,
      );
    });
  });

  it('numericEdit_validNumber_submitsNumber', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    const firstRow = document.querySelector('tbody tr')!;
    const cells = firstRow.querySelectorAll('td');
    await userEvent.click(cells[4]);

    await waitFor(() => expect(screen.getByText('Edit cell')).toBeInTheDocument());
    const input = document.querySelector('input[type="number"]') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '0' } });
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => {
      expect(dbAdminApi.patchRow).toHaveBeenCalledWith(
        'Workflow',
        ['abc-123'],
        'Score',
        0,
      );
    });
  });

  it('delete_callsConfirmAndDeleteRow', async () => {
    wrap(<DbViewerPage />);

    await waitFor(() => screen.getByText('Workflows'));
    await userEvent.click(screen.getByRole('button', { name: /Workflows/ }));
    await waitFor(() => screen.getByText('My Workflow'));

    await userEvent.click(screen.getByTitle('Delete'));

    await waitFor(() => {
      expect(dbAdminApi.deleteRow).toHaveBeenCalledWith('Workflow', ['abc-123']);
    });
    expect(confirmDialog).toHaveBeenCalled();
  });

  it('adminOnly_operator_redirectsToDashboard', () => {
    useAuthStore.setState({ isAuthenticated: true, username: 'op', role: 'Operator' });

    // Replicate the AdminOnly guard from App.tsx
    function AdminOnly({ children }: { children: React.ReactNode }) {
      const role = useAuthStore((s) => s.role);
      return role === 'Admin' ? <>{children}</> : <Navigate to="/" replace />;
    }

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={['/database']}>
          <Routes>
            <Route path="/" element={<div>Dashboard</div>} />
            <Route
              path="/database"
              element={
                <AdminOnly>
                  <DbViewerPage />
                </AdminOnly>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(screen.getByText('Dashboard')).toBeInTheDocument();
    expect(screen.queryByText('Select a table')).not.toBeInTheDocument();
  });
});
