import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { QueryPane } from '../../../components/dbviewer/QueryPane';
import { dbAdminApi, type DbAdminQueryResponse } from '../../../api/dbadmin';
import { useAuthStore } from '../../../stores/authStore';
import { clearLocalAuthBoundary } from '../../../security/authBoundary';
import {
  DB_ADMIN_QUERY_DRAFT_KEY,
  DB_ADMIN_QUERY_HISTORY_KEY,
} from '../../../security/sensitiveBrowserState';

vi.mock('../../../api/dbadmin', () => ({
  dbAdminApi: {
    getInfo: vi.fn(),
    query: vi.fn(),
    // The full module surface is mocked so this file's tests don't pull in real network calls
    // when QueryPane indirectly imports from dbadmin.
    getTables: vi.fn(),
    getRows: vi.fn(),
    patchRow: vi.fn(),
    deleteRow: vi.fn(),
  },
}));

// CodeMirror measures layout via Range.getClientRects, which jsdom does not implement.
// Mocking @uiw/react-codemirror as a plain textarea keeps these tests on QueryPane
// behaviour (wiring, run, history, mode toggle) instead of the editor internals.
vi.mock('@uiw/react-codemirror', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) => (
    <textarea
      data-testid="sql-editor"
      value={value}
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value)}
    />
  ),
}));

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

const INFO_READONLY = {
  provider: 'postgres' as const,
  allowWriteQueries: false,
  queryTimeoutSeconds: 30,
  queryMaxRows: 10_000,
};

const INFO_WRITE = { ...INFO_READONLY, allowWriteQueries: true };

describe('QueryPane', () => {
  beforeEach(() => {
    vi.mocked(dbAdminApi.getInfo).mockResolvedValue(INFO_READONLY);
    vi.mocked(dbAdminApi.query).mockResolvedValue({
      columns: [{ name: 'Id', type: 'int' }],
      rows: [[1]],
      rowsAffected: null,
      durationMs: 5,
      truncated: false,
      mode: 'read',
    });
    globalThis.localStorage.clear();
    globalThis.sessionStorage.clear();
    useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: false });
  });

  it('rendersProviderBadge_fromInfoEndpoint', async () => {
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());
  });

  it('writeToggle_disabled_whenServerForbidsWrites', async () => {
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    const writeBtn = screen.getByRole('button', { name: /^Write$/ });
    expect(writeBtn).toBeDisabled();
  });

  it('writeToggle_enabled_whenServerAllowsWrites', async () => {
    vi.mocked(dbAdminApi.getInfo).mockResolvedValue(INFO_WRITE);
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    const writeBtn = screen.getByRole('button', { name: /^Write$/ });
    expect(writeBtn).not.toBeDisabled();
  });

  it('runButton_disabled_whenSqlIsEmpty', async () => {
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());
    const runBtn = screen.getByRole('button', { name: /^Run$/ });
    expect(runBtn).toBeDisabled();
  });

  it('runButton_callsQueryEndpoint_withReadModeAndTrimmedSql', async () => {
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    const editor = screen.getByTestId('sql-editor');
    fireEvent.change(editor, { target: { value: '  SELECT 1  ' } });

    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));

    await waitFor(() => {
      expect(dbAdminApi.query).toHaveBeenCalledWith('SELECT 1', 'read');
    });
  });

  it('resultTable_rendersColumnsAndRows', async () => {
    vi.mocked(dbAdminApi.query).mockResolvedValue({
      columns: [{ name: 'Username', type: 'string' }],
      rows: [['alice'], ['bob']],
      rowsAffected: null,
      durationMs: 12,
      truncated: false,
      mode: 'read',
    });

    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('sql-editor'), { target: { value: 'SELECT Username FROM Users' } });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));

    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());
    expect(screen.getByText('bob')).toBeInTheDocument();
    expect(screen.getByText('Username')).toBeInTheDocument();
  });

  it('resultTable_columnsCanBeResized', async () => {
    vi.mocked(dbAdminApi.query).mockResolvedValue({
      columns: [{ name: 'Username', type: 'string' }],
      rows: [['alice']],
      rowsAffected: null,
      durationMs: 12,
      truncated: false,
      mode: 'read',
    });

    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('sql-editor'), { target: { value: 'SELECT Username FROM Users' } });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));
    await waitFor(() => expect(screen.getByText('alice')).toBeInTheDocument());

    const handle = screen.getByRole('separator', { name: 'Resize Username column' });
    const column = document.querySelector('colgroup col') as HTMLElement;
    expect(column).toHaveStyle({ width: '200px' });

    fireEvent.keyDown(handle, { key: 'ArrowLeft' });
    expect(column).toHaveStyle({ width: '184px' });
  });

  it('errorState_rendersServerMessage', async () => {
    vi.mocked(dbAdminApi.query).mockRejectedValue(new Error('Statement starts with UPDATE which is not allowed'));

    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('sql-editor'), { target: { value: 'UPDATE Users SET x = 1' } });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));

    await waitFor(() => expect(screen.getByText(/UPDATE which is not allowed/)).toBeInTheDocument());
  });

  it('writeMode_opensConfirmDialog_andRequiresExactPhrase', async () => {
    vi.mocked(dbAdminApi.getInfo).mockResolvedValue(INFO_WRITE);
    vi.mocked(dbAdminApi.query).mockResolvedValue({
      columns: [],
      rows: [],
      rowsAffected: 1,
      durationMs: 8,
      truncated: false,
      mode: 'write',
    });

    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    // Switch to write mode
    await userEvent.click(screen.getByRole('button', { name: /^Write$/ }));
    fireEvent.change(screen.getByTestId('sql-editor'), { target: { value: 'UPDATE Users SET IsActive = 0' } });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));

    // Dialog opens; confirm button is disabled until the phrase is typed exactly
    const confirmBtn = await screen.findByRole('button', { name: /Yes, run it/ });
    expect(confirmBtn).toBeDisabled();

    const phraseInput = document.querySelector('input[type="text"]') as HTMLInputElement;
    expect(phraseInput).not.toBeNull();
    fireEvent.change(phraseInput, { target: { value: 'ALLOW WRITE' } });
    expect(confirmBtn).not.toBeDisabled();

    await userEvent.click(confirmBtn);
    await waitFor(() => {
      expect(dbAdminApi.query).toHaveBeenCalledWith('UPDATE Users SET IsActive = 0', 'write');
    });
  });

  it('history_persistsAcrossPaneRemount_withinSessionStorage', async () => {
    globalThis.sessionStorage.setItem(
      'nodepilot.dbAdmin.queryHistory',
      JSON.stringify(['SELECT 1', 'SELECT 2']),
    );
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    // The history button shows the count from this tab's session storage.
    expect(screen.getByRole('button', { name: /History \(2\)/ })).toBeInTheDocument();
  });

  it('history_appendsAfterSuccessfulQuery', async () => {
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('sql-editor'), { target: { value: 'SELECT 1' } });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /History \(1\)/ })).toBeInTheDocument();
    });

    const stored = JSON.parse(globalThis.sessionStorage.getItem('nodepilot.dbAdmin.queryHistory') ?? '[]') as string[];
    expect(stored).toEqual(['SELECT 1']);
    expect(globalThis.localStorage.getItem('nodepilot.dbAdmin.queryHistory')).toBeNull();
  });

  it('inFlightQueryFromPreviousIdentity_cannotRestoreHistoryOrResultsAfterBoundary', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    let resolveQuery!: (result: DbAdminQueryResponse) => void;
    const pendingQuery = new Promise<DbAdminQueryResponse>((resolve) => {
      resolveQuery = resolve;
    });
    vi.mocked(dbAdminApi.query).mockReturnValueOnce(pendingQuery);

    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('sql-editor'), {
      target: { value: 'SELECT user_a_secret' },
    });
    await userEvent.click(screen.getByRole('button', { name: /^Run$/ }));
    await waitFor(() => expect(dbAdminApi.query).toHaveBeenCalledWith(
      'SELECT user_a_secret',
      'read',
    ));

    clearLocalAuthBoundary();
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
    });

    await act(async () => {
      resolveQuery({
        columns: [{ name: 'Secret', type: 'text' }],
        rows: [['user-a-result']],
        rowsAffected: null,
        durationMs: 5,
        truncated: false,
        mode: 'read',
      });
      await pendingQuery;
      await Promise.resolve();
    });

    expect(useAuthStore.getState().userId).toBe('u-b');
    expect(globalThis.sessionStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
    expect(globalThis.sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(screen.queryByText('user-a-result')).not.toBeInTheDocument();
  });
});
