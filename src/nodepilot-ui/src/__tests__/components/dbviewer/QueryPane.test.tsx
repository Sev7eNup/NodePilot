import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { QueryPane } from '../../../components/dbviewer/QueryPane';
import { dbAdminApi } from '../../../api/dbadmin';

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

// CodeMirror layout-measurement code uses Range.getClientRects, which jsdom doesn't
// support. We mock the @uiw/react-codemirror component to a plain textarea — keeps
// these tests focused on QueryPane's behaviour (wiring, run, history, mode-toggle)
// rather than the editor's internals.
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

  it('history_persistsAcrossPaneRemount_viaLocalStorage', async () => {
    globalThis.localStorage.setItem(
      'nodepilot.dbAdmin.queryHistory',
      JSON.stringify(['SELECT 1', 'SELECT 2']),
    );
    wrap(<QueryPane />);
    await waitFor(() => expect(screen.getByText('postgres')).toBeInTheDocument());

    // The history button shows the count from localStorage.
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

    const stored = JSON.parse(globalThis.localStorage.getItem('nodepilot.dbAdmin.queryHistory') ?? '[]') as string[];
    expect(stored).toEqual(['SELECT 1']);
  });
});
