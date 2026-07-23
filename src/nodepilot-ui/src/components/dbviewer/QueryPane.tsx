import { Close, DataBase, History, Play, SecurityServices, WarningAltFilled } from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import CodeMirror, { type ReactCodeMirrorRef } from '@uiw/react-codemirror';
import { StreamLanguage } from '@codemirror/language';
import { standardSQL } from '@codemirror/legacy-modes/mode/sql';
import { keymap } from '@codemirror/view';
import { dbAdminApi, type DbAdminQueryResponse } from '../../api/dbadmin';
import { useThemeStore, resolveTheme } from '../../stores/themeStore';
import { ResizeHandle, useResizableColumns, type ResizableColumn } from './useResizableColumns';

const HISTORY_KEY = 'nodepilot.dbAdmin.queryHistory';
const DRAFT_SQL_KEY = 'nodepilot.dbAdmin.queryDraft';
const DRAFT_MODE_KEY = 'nodepilot.dbAdmin.queryMode';
const HISTORY_LIMIT = 20;
const WRITE_CONFIRM_PHRASE = 'ALLOW WRITE';

interface Props {
  /**
   * Inserts a table name at the editor cursor. Wired to the Tables sidebar so an
   * operator can click a name instead of typing it. The ref-based approach lets
   * the parent invoke us imperatively without remounting the editor.
   */
  insertSignal?: { value: string; nonce: number };
}

type Mode = 'read' | 'write';

function loadHistory(): string[] {
  try {
    const raw = globalThis.localStorage.getItem(HISTORY_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

function saveHistory(items: string[]): void {
  try {
    globalThis.localStorage.setItem(HISTORY_KEY, JSON.stringify(items.slice(0, HISTORY_LIMIT)));
  } catch {
    // localStorage may be disabled (private browsing, quota). History is a nice-to-have,
    // not a correctness boundary — silently drop and move on.
  }
}

/** Read/write the in-progress SQL so navigating away from the Query pane and back
 *  doesn't lose what the user was typing. Stored separately from history because
 *  history is "things I'd want to re-run" and the draft is "things I was typing". */
function loadDraftSql(): string {
  try {
    return globalThis.localStorage.getItem(DRAFT_SQL_KEY) ?? '';
  } catch {
    return '';
  }
}

function saveDraftSql(value: string): void {
  try {
    globalThis.localStorage.setItem(DRAFT_SQL_KEY, value);
  } catch {
    // Same fallback rationale as saveHistory.
  }
}

function loadDraftMode(): 'read' | 'write' {
  try {
    return globalThis.localStorage.getItem(DRAFT_MODE_KEY) === 'write' ? 'write' : 'read';
  } catch {
    return 'read';
  }
}

export function QueryPane({ insertSignal }: Readonly<Props>) {
  const { t } = useTranslation(['database', 'common']);
  const theme = useThemeStore((s) => s.theme);
  const isDark = resolveTheme(theme) === 'dark';

  const [sql, setSqlState] = useState<string>(() => loadDraftSql());
  const [mode, setModeState] = useState<Mode>(() => loadDraftMode());
  const [showHistory, setShowHistory] = useState(false);
  const [history, setHistory] = useState<string[]>(() => loadHistory());
  const [writeConfirmInput, setWriteConfirmInput] = useState('');
  const [showWriteDialog, setShowWriteDialog] = useState(false);

  // Persist sql + mode to localStorage on every change so navigating to a table
  // and back doesn't drop the in-progress query.
  const setSql = useCallback((v: string) => {
    setSqlState(v);
    saveDraftSql(v);
  }, []);
  const setMode = useCallback((m: Mode) => {
    setModeState(m);
    try { globalThis.localStorage.setItem(DRAFT_MODE_KEY, m); } catch { /* see saveHistory rationale */ }
  }, []);

  const editorRef = useRef<ReactCodeMirrorRef>(null);

  const { data: info } = useQuery({
    queryKey: ['dbadmin', 'info'],
    queryFn: () => dbAdminApi.getInfo(),
    staleTime: 60_000,
  });

  // If the server doesn't allow writes, force read mode — keeps the toggle from showing
  // an enabled state that the server would just reject.
  useEffect(() => {
    if (info && !info.allowWriteQueries && mode === 'write') setMode('read');
  }, [info, mode]);

  const queryMutation = useMutation<DbAdminQueryResponse, Error, { sql: string; mode: Mode }>({
    mutationFn: ({ sql: s, mode: m }) => dbAdminApi.query(s, m),
    onSuccess: (_, vars) => {
      // Push successful queries into history (de-dup, most-recent first).
      setHistory((prev) => {
        const next = [vars.sql, ...prev.filter((q) => q !== vars.sql)].slice(0, HISTORY_LIMIT);
        saveHistory(next);
        return next;
      });
    },
  });

  // Imperatively insert a string at the current cursor position. Called when the
  // sidebar fires an insertSignal — used to drop a table name into the editor.
  // We quote with double-quotes because EF Core stores entity-set names in
  // PascalCase ("Credential", "Workflow"…) and Postgres folds unquoted identifiers
  // to lowercase, so an unquoted "from Credential" hits "credential" which doesn't
  // exist. SQL Server (with default QUOTED_IDENTIFIER ON) and SQLite both accept
  // ANSI double-quoted identifiers too, so one form covers all three providers.
  useEffect(() => {
    if (!insertSignal) return;
    const view = editorRef.current?.view;
    if (!view) return;
    const pos = view.state.selection.main.head;
    const needsLeadingSpace = pos > 0 && !/\s|\(/.test(view.state.doc.sliceString(pos - 1, pos));
    const quoted = `"${insertSignal.value.replaceAll('"', '""')}"`;
    const insertion = (needsLeadingSpace ? ' ' : '') + quoted;
    view.dispatch({
      changes: { from: pos, insert: insertion },
      selection: { anchor: pos + insertion.length },
    });
    view.focus();
  }, [insertSignal]);

  const runQuery = useCallback(() => {
    const trimmed = sql.trim();
    if (!trimmed) return;
    if (mode === 'write' && !showWriteDialog) {
      setShowWriteDialog(true);
      setWriteConfirmInput('');
      return;
    }
    queryMutation.mutate({ sql: trimmed, mode });
  }, [sql, mode, queryMutation, showWriteDialog]);

  const cmExtensions = useMemo(() => [
    StreamLanguage.define(standardSQL),
    keymap.of([
      {
        key: 'Mod-Enter',
        run: () => { runQuery(); return true; },
      },
    ]),
  ], [runQuery]);

  const writeAllowed = info?.allowWriteQueries ?? false;
  const canRun = sql.trim().length > 0 && !queryMutation.isPending;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="flex items-center gap-3 px-5 py-3 border-b border-outline-variant/20 shrink-0">
        <div className="flex items-center gap-2">
          <DataBase size={16} className="text-on-surface-variant" />
          <h2 className="font-headline font-bold text-base text-on-surface">{t('database:query.title')}</h2>
        </div>
        {info && (
          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] font-medium bg-surface-container text-on-surface-variant font-mono">
            {info.provider}
          </span>
        )}
        <div className="ml-auto flex items-center gap-2">
          <button
            type="button"
            onClick={() => setShowHistory((s) => !s)}
            disabled={history.length === 0}
            className="inline-flex items-center gap-1 px-2 py-1 rounded text-xs text-on-surface-variant hover:bg-surface-highest transition-colors disabled:opacity-40"
            title={t('database:query.historyTitle')}
          >
            <History size={13} />
            {t('database:query.history', { count: history.length })}
          </button>
        </div>
      </div>
      {/* Editor + toolbar */}
      <div className="flex flex-col gap-2 p-4 border-b border-outline-variant/20 shrink-0">
        <div className="border border-outline-variant/40 rounded-md overflow-hidden bg-surface-lowest">
          <CodeMirror
            ref={editorRef}
            value={sql}
            onChange={(v) => setSql(v)}
            theme={isDark ? 'dark' : 'light'}
            extensions={cmExtensions}
            placeholder={t('database:query.placeholder')}
            basicSetup={{
              lineNumbers: true,
              highlightActiveLine: false,
              foldGutter: false,
              autocompletion: false,
              bracketMatching: true,
              closeBrackets: true,
            }}
            height="180px"
          />
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={runQuery}
            disabled={!canRun}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-primary text-on-primary text-sm font-medium hover:bg-primary/90 disabled:opacity-50 disabled:cursor-not-allowed"
            title={t('database:query.runTitle')}
          >
            <Play size={14} />
            {queryMutation.isPending ? t('database:query.running') : t('database:query.run')}
          </button>

          {/* Read/Write toggle — write only when the server allows it */}
          <div className="inline-flex rounded-md border border-outline-variant/40 overflow-hidden text-xs">
            <button
              type="button"
              onClick={() => setMode('read')}
              className={`px-2.5 py-1 ${mode === 'read'
                ? 'bg-primary text-on-primary'
                : 'bg-surface-lowest text-on-surface-variant hover:bg-surface-low'}`}
            >
              {t('database:query.readMode')}
            </button>
            <button
              type="button"
              onClick={() => writeAllowed && setMode('write')}
              disabled={!writeAllowed}
              title={writeAllowed ? '' : t('database:query.writeDisabled')}
              className={`px-2.5 py-1 ${mode === 'write'
                ? 'bg-amber-600 text-white'
                : 'bg-surface-lowest text-on-surface-variant hover:bg-surface-low disabled:opacity-50'}`}
            >
              {t('database:query.writeMode')}
            </button>
          </div>

          {mode === 'write' && writeAllowed && (
            <span className="inline-flex items-center gap-1 text-[11px] text-amber-700">
              <SecurityServices size={12} />
              {t('database:query.writeWarning')}
            </span>
          )}

          {/* Visible hint when write-mode is server-disabled. The button itself only
              carries a native title="..." tooltip, which means an operator who can't
              click the toggle has no obvious clue why. This line tells them exactly
              which setting to flip. */}
          {info && !writeAllowed && (
            <span className="inline-flex items-center gap-1 text-[11px] text-on-surface-variant">
              <SecurityServices size={12} className="text-outline" />
              {t('database:query.writeDisabled')}
            </span>
          )}

          <span className="ml-auto text-[11px] text-outline">
            {t('database:query.shortcutHint')}
          </span>
        </div>
      </div>
      {/* History dropdown */}
      {showHistory && history.length > 0 && (
        <div className="border-b border-outline-variant/20 bg-surface-low shrink-0 max-h-48 overflow-y-auto">
          <div className="flex items-center justify-between px-4 py-2 border-b border-outline-variant/20">
            <span className="text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider">
              {t('database:query.historyTitle')}
            </span>
            <button
              type="button"
              onClick={() => {
                setHistory([]);
                saveHistory([]);
                setShowHistory(false);
              }}
              className="text-[11px] text-on-surface-variant hover:text-on-surface"
            >
              {t('database:query.clearHistory')}
            </button>
          </div>
          {history.map((q, i) => (
            <button
              key={`${i}-${q.slice(0, 24)}`}
              type="button"
              onClick={() => { setSql(q); setShowHistory(false); }}
              className="w-full text-left px-4 py-1.5 font-mono text-xs text-on-surface-variant truncate hover:bg-surface-highest"
              title={q}
            >
              {q}
            </button>
          ))}
        </div>
      )}
      {/* Results / error */}
      <div className="flex-1 overflow-auto">
        {queryMutation.error && (
          <div className="m-4 p-3 rounded-md bg-error-container/40 border border-error/40 text-sm text-error">
            <div className="flex items-start gap-2">
              <WarningAltFilled size={14} className="mt-0.5 shrink-0" />
              <div className="flex-1 min-w-0">
                <p className="font-semibold mb-0.5">{t('database:query.errorTitle')}</p>
                <p className="font-mono text-xs whitespace-pre-wrap break-words">
                  {queryMutation.error.message}
                </p>
              </div>
            </div>
          </div>
        )}

        {queryMutation.data && <ResultTable data={queryMutation.data} />}

        {!queryMutation.data && !queryMutation.error && (
          <div className="flex flex-col items-center justify-center h-full text-on-surface-variant gap-2">
            <Play size={28} className="opacity-30" />
            <p className="text-sm font-label">{t('database:query.idleHint')}</p>
          </div>
        )}
      </div>
      {showWriteDialog && (
        <WriteConfirmDialog
          phrase={WRITE_CONFIRM_PHRASE}
          input={writeConfirmInput}
          onInput={setWriteConfirmInput}
          onCancel={() => { setShowWriteDialog(false); setWriteConfirmInput(''); }}
          onConfirm={() => {
            setShowWriteDialog(false);
            setWriteConfirmInput('');
            queryMutation.mutate({ sql: sql.trim(), mode: 'write' });
          }}
        />
      )}
    </div>
  );
}

function ResultTable({ data }: Readonly<{ data: DbAdminQueryResponse }>) {
  const { t } = useTranslation(['database', 'common']);
  const resizableColumns = data.columns.map<ResizableColumn>((column, index) => ({
    key: `${index}:${column.name}`,
    defaultWidth: 200,
  }));
  const { getWidth, resizeBy, startResize, totalWidth } = useResizableColumns(resizableColumns);

  return (
    <div className="p-4">
      <div className="flex flex-wrap items-center gap-3 mb-2 text-xs text-on-surface-variant">
        {data.rowsAffected !== null && (
          <span>{t('database:query.rowsAffected', { count: data.rowsAffected })}</span>
        )}
        <span>{t('database:query.durationMs', { ms: data.durationMs })}</span>
        <span className="font-mono">{data.mode}</span>
        {data.truncated && (
          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium bg-amber-100 text-amber-800">
            <WarningAltFilled size={11} />
            {t('database:query.truncated')}
          </span>
        )}
      </div>
      {data.columns.length === 0 ? (
        <p className="text-sm text-on-surface-variant">{t('database:query.noResultSet')}</p>
      ) : (
        <div className="overflow-x-auto bg-surface-lowest rounded-md border border-outline-variant/20">
          <table className="text-xs font-mono table-fixed" style={{ width: totalWidth }}>
            <colgroup>
              {resizableColumns.map((column) => (
                <col key={column.key} style={{ width: getWidth(column) }} />
              ))}
            </colgroup>
            <thead className="np-col-header text-left">
              <tr>
                {data.columns.map((c, index) => (
                  <th key={`${index}:${c.name}`} className="relative px-3 py-2 font-semibold text-on-surface-variant whitespace-nowrap overflow-hidden">
                    <span className="block truncate">
                      {c.name}
                      <span className="ml-1 text-[10px] font-normal text-outline">({c.type})</span>
                    </span>
                    <ResizeHandle
                      label={t('database:resizeColumn', { name: c.name })}
                      column={resizableColumns[index]}
                      onPointerDown={startResize}
                      onResizeBy={resizeBy}
                    />
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/20">
              {data.rows.map((row, i) => (
                <tr key={i} className="hover:bg-surface-low">
                  {row.map((cell, j) => (
                    <td key={j} className="px-3 py-1.5 whitespace-nowrap text-on-surface-variant truncate overflow-hidden">
                      {renderCell(cell)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function renderCell(value: unknown): React.ReactNode {
  if (value === null || value === undefined) {
    return <span className="text-outline italic">null</span>;
  }
  if (typeof value === 'object') {
    return <span title={JSON.stringify(value)}>{JSON.stringify(value).slice(0, 60)}</span>;
  }
  const s = String(value);
  if (s.length > 80) return <span title={s}>{s.slice(0, 80)}…</span>;
  return s;
}

function WriteConfirmDialog({
  phrase, input, onInput, onCancel, onConfirm,
}: Readonly<{
  phrase: string;
  input: string;
  onInput: (v: string) => void;
  onCancel: () => void;
  onConfirm: () => void;
}>) {
  const { t } = useTranslation(['database', 'common']);
  const ok = input === phrase;

  return (
    <div
      className="fixed inset-0 bg-black/30 backdrop-blur-sm flex items-center justify-center z-50"
      onClick={onCancel}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="bg-surface-lowest rounded-lg shadow-xl p-6 w-full max-w-md"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold text-on-surface flex items-center gap-2">
            <SecurityServices size={18} className="text-amber-600" />
            {t('database:query.confirmWriteTitle')}
          </h3>
          <button onClick={onCancel} className="p-1 text-on-surface-variant hover:bg-surface-container rounded">
            <Close size={16} />
          </button>
        </div>
        <p className="text-sm text-on-surface-variant mb-3">
          {t('database:query.confirmWriteHint')}
        </p>
        <p className="text-xs text-on-surface-variant mb-1">
          {t('database:query.confirmWritePhrasePrompt', { phrase })}
        </p>
        <input
          type="text"
          value={input}
          onChange={(e) => onInput(e.target.value)}
          autoFocus
          className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-amber-500"
        />
        <div className="flex justify-end gap-2 mt-4">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
          >
            {t('common:cancel')}
          </button>
          <button
            onClick={onConfirm}
            disabled={!ok}
            className="px-4 py-2 bg-amber-600 text-white text-sm rounded-md hover:bg-amber-700 disabled:opacity-50"
          >
            {t('database:query.confirmWriteButton')}
          </button>
        </div>
      </div>
    </div>
  );
}
