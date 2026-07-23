import {
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronUp,
  DoubleChevronLeft,
  DoubleChevronRight,
  Renew,
  TrashCan,
} from '@carbon/icons-react';
import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { dbAdminApi } from '../../api/dbadmin';
import { EditCellDialog } from './EditCellDialog';
import { ResizeHandle, useResizableColumns, type ResizableColumn } from './useResizableColumns';
import type { DbAdminTableInfo, DbAdminColumnInfo } from '../../api/dbadmin';
import { toast } from '../../stores/toastStore';
import { confirmDialog } from '../../stores/confirmStore';

interface Props {
  table: DbAdminTableInfo;
}

const PAGE_SIZE_OPTIONS = [50, 100, 200];

export function TableGrid({ table }: Readonly<Props>) {
  const { t } = useTranslation(['database', 'common']);
  const queryClient = useQueryClient();

  const [skip, setSkip] = useState(0);
  const [take, setTake] = useState(100);
  const [orderBy, setOrderBy] = useState<string | null>(null);
  const [desc, setDesc] = useState(false);

  const [editState, setEditState] = useState<{
    row: Record<string, unknown>;
    column: DbAdminColumnInfo;
  } | null>(null);

  const queryKey = ['dbadmin', 'rows', table.name, skip, take, orderBy, desc];

  const { data, isFetching, refetch } = useQuery({
    queryKey,
    queryFn: () => dbAdminApi.getRows(table.name, { skip, take, orderBy: orderBy ?? undefined, desc }),
  });

  const rows = data?.rows ?? [];
  const total = data?.total ?? 0;
  const pageCount = Math.max(1, Math.ceil(total / take));
  const currentPage = Math.floor(skip / take) + 1;

  const patchMutation = useMutation({
    mutationFn: ({ pk, column, value }: { pk: string[]; column: string; value: unknown }) =>
      dbAdminApi.patchRow(table.name, pk, column, value),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dbadmin', 'rows', table.name] });
      queryClient.invalidateQueries({ queryKey: ['dbadmin', 'tables'] });
      setEditState(null);
    },
    onError: (err: Error) => toast.error(t('database:errorUpdate') + ': ' + err.message),
  });

  const deleteMutation = useMutation({
    mutationFn: (pk: string[]) => dbAdminApi.deleteRow(table.name, pk),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dbadmin', 'rows', table.name] });
      queryClient.invalidateQueries({ queryKey: ['dbadmin', 'tables'] });
    },
    onError: (err: Error) => toast.error(t('database:errorDelete') + ': ' + err.message),
  });

  const visibleColumns = table.columns;
  const resizableColumns = visibleColumns.map<ResizableColumn>((column) => ({
    key: column.name,
    defaultWidth: defaultColumnWidth(column),
  }));
  const { getWidth, resizeBy, startResize, totalWidth } = useResizableColumns(resizableColumns);

  function getPkValues(row: Record<string, unknown>): string[] {
    return table.pkColumns.map((pk) => String(row[pk] ?? ''));
  }

  function handleHeaderClick(colName: string) {
    if (orderBy === colName) {
      setDesc(!desc);
    } else {
      setOrderBy(colName);
      setDesc(false);
    }
    setSkip(0);
  }

  function handleCellClick(row: Record<string, unknown>, col: DbAdminColumnInfo) {
    if (!table.capabilities.canUpdate) return;
    if (col.isReadOnly) return;
    if (col.isMasked) return;
    setEditState({ row, column: col });
  }

  async function handleDelete(row: Record<string, unknown>) {
    const pk = getPkValues(row);
    const cascadeMsg = table.cascadeDeletesTo.length > 0
      ? `\n${t('database:cascadeWarning', { tables: table.cascadeDeletesTo.join(', ') })}`
      : '';
    if (await confirmDialog({ message: `${t('database:deleteConfirm', { table: table.displayName })}${cascadeMsg}`, danger: true })) {
      deleteMutation.mutate(pk);
    }
  }

  function handleSave(newValue: unknown) {
    if (!editState) return;
    const pk = getPkValues(editState.row);
    patchMutation.mutate({ pk, column: editState.column.name, value: newValue });
  }

  function renderCellValue(value: unknown, col: DbAdminColumnInfo): React.ReactNode {
    if (value === null || value === undefined) {
      return <span className="text-outline italic text-[10px]">null</span>;
    }
    if (col.isMasked) {
      return <span className="text-on-surface-variant italic text-[11px]">***</span>;
    }
    const s = typeof value === 'object' ? JSON.stringify(value) : String(value);
    if (s.length > 80) return <span className="font-mono text-xs" title={s}>{s.slice(0, 80)}…</span>;
    if (col.clrType.startsWith('datetime')) {
      return <span className="font-mono text-xs">{formatDateTime(s)}</span>;
    }
    if (col.clrType.startsWith('boolean')) {
      return <span className={`text-xs font-semibold ${value ? 'text-green-700' : 'text-red-600'}`}>{String(value)}</span>;
    }
    return <span className="text-sm">{s}</span>;
  }

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="flex items-center gap-3 px-5 py-3 border-b border-outline-variant/20 shrink-0">
        <div>
          <h2 className="font-headline font-bold text-base text-on-surface">{table.displayName}</h2>
          <p className="text-xs text-on-surface-variant font-mono">{table.name}</p>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <span className="text-xs text-on-surface-variant">
            {t('database:showing', {
              from: total === 0 ? 0 : skip + 1,
              to: Math.min(skip + take, total),
              total,
            })}
          </span>
          <button
            onClick={() => refetch()}
            className={`p-1.5 rounded-md text-on-surface-variant hover:bg-surface-highest transition-colors ${isFetching ? 'opacity-60' : ''}`}
            title={t('common:reload')}
          >
            <Renew size={14} className={isFetching ? 'animate-spin' : ''} />
          </button>
        </div>
      </div>
      {/* Table */}
      <div className="flex-1 overflow-auto">
        <table
          className="text-left border-collapse table-fixed"
          style={{ width: totalWidth + (table.capabilities.canDelete ? 40 : 0) }}
        >
          <colgroup>
            {resizableColumns.map((column) => (
              <col key={column.key} style={{ width: getWidth(column) }} />
            ))}
            {table.capabilities.canDelete && <col style={{ width: 40 }} />}
          </colgroup>
          <thead className="sticky top-0 z-10 bg-surface-low">
            <tr>
              {visibleColumns.map((col, index) => (
                <th
                  key={col.name}
                  onClick={() => handleHeaderClick(col.name)}
                  className="relative px-3 py-2 text-[10px] font-label font-semibold uppercase tracking-wider text-on-surface-variant border-b border-outline-variant/20 whitespace-nowrap select-none cursor-pointer hover:text-on-surface"
                >
                  <span className="inline-flex max-w-full items-center gap-1 overflow-hidden">
                    <span className="truncate">
                    {col.name}
                    </span>
                    {col.isPrimaryKey && <span className="text-primary text-[8px] font-bold ml-0.5">PK</span>}
                    {col.isReadOnly && !col.isPrimaryKey && <span className="text-outline text-[8px] ml-0.5">RO</span>}
                    {orderBy === col.name && (
                      desc ? <ChevronDown size={11} /> : <ChevronUp size={11} />
                    )}
                  </span>
                  <ResizeHandle
                    label={t('database:resizeColumn', { name: col.name })}
                    column={resizableColumns[index]}
                    onPointerDown={startResize}
                    onResizeBy={resizeBy}
                  />
                </th>
              ))}
              {table.capabilities.canDelete && (
                <th className="px-3 py-2 border-b border-outline-variant/20 w-10" />
              )}
            </tr>
          </thead>
          <tbody className="divide-y divide-outline-variant/15">
            {isFetching && rows.length === 0 && (
              <tr>
                <td colSpan={visibleColumns.length + 1} className="px-4 py-10 text-center text-sm text-on-surface-variant">
                  {t('database:loading')}
                </td>
              </tr>
            )}
            {!isFetching && rows.length === 0 && (
              <tr>
                <td colSpan={visibleColumns.length + 1} className="px-4 py-10 text-center text-sm text-on-surface-variant">
                  {t('database:noRows')}
                </td>
              </tr>
            )}
            {rows.map((row, ri) => (
              <tr key={ri} className={`hover:bg-surface-low transition-colors ${deleteMutation.isPending ? 'opacity-60' : ''}`}>
                {visibleColumns.map((col) => {
                  const canEdit = table.capabilities.canUpdate && !col.isReadOnly && !col.isMasked;
                  const val = row[col.name];
                  return (
                    <td
                      key={col.name}
                      onClick={() => canEdit ? handleCellClick(row, col) : undefined}
                      title={canEdit ? undefined : col.isReadOnly ? t('database:readonlyColumn') : col.isMasked ? t('database:maskedValue') : undefined}
                      className={`px-3 py-1.5 text-sm text-on-surface truncate overflow-hidden align-middle ${
                        canEdit
                          ? 'cursor-pointer hover:bg-primary-fixed/40 rounded'
                          : col.isReadOnly || col.isMasked
                          ? 'cursor-default'
                          : ''
                      }`}
                    >
                      {renderCellValue(val, col)}
                    </td>
                  );
                })}
                {table.capabilities.canDelete && (
                  <td className="px-2 py-1 align-middle">
                    <button
                      onClick={() => handleDelete(row)}
                      disabled={deleteMutation.isPending}
                      className="p-1 rounded text-red-500 hover:bg-red-50 hover:text-red-700 transition-colors disabled:opacity-40"
                      title={t('database:delete')}
                    >
                      <TrashCan size={13} />
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {/* Pagination */}
      <div className="flex items-center justify-between px-5 py-2.5 border-t border-outline-variant/20 bg-surface-low shrink-0">
        <div className="flex items-center gap-2">
          <span className="text-[11px] text-on-surface-variant font-label">
            {t('database:rowsPerPage')}
          </span>
          <select
            value={take}
            onChange={(e) => { setTake(Number(e.target.value)); setSkip(0); }}
            className="text-xs px-2 py-1 border border-outline-variant rounded bg-surface text-on-surface focus:outline-none focus:ring-1 focus:ring-primary/50"
          >
            {PAGE_SIZE_OPTIONS.map((n) => (
              <option key={n} value={n}>{n}</option>
            ))}
          </select>
        </div>
        <div className="flex items-center gap-1">
          <PagBtn disabled={skip === 0} onClick={() => setSkip(0)} title={t('database:firstPage')}>
            <DoubleChevronLeft size={14} />
          </PagBtn>
          <PagBtn disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))} title={t('database:prevPage')}>
            <ChevronLeft size={14} />
          </PagBtn>
          <span className="px-3 py-1 text-xs text-on-surface-variant tabular-nums">
            {t('database:pageOf', { current: currentPage, total: pageCount })}
          </span>
          <PagBtn disabled={skip + take >= total} onClick={() => setSkip(skip + take)} title={t('database:nextPage')}>
            <ChevronRight size={14} />
          </PagBtn>
          <PagBtn disabled={skip + take >= total} onClick={() => setSkip((pageCount - 1) * take)} title={t('database:lastPage')}>
            <DoubleChevronRight size={14} />
          </PagBtn>
        </div>
      </div>
      {/* Edit Dialog */}
      {editState && (
        <EditCellDialog
          tableName={table.name}
          column={editState.column}
          currentValue={editState.row[editState.column.name]}
          onSave={handleSave}
          onClose={() => setEditState(null)}
          isSaving={patchMutation.isPending}
        />
      )}
    </div>
  );
}

function PagBtn({ disabled, onClick, title, children }: Readonly<{
  disabled: boolean;
  onClick: () => void;
  title: string;
  children: React.ReactNode;
}>) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="p-1 rounded text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors"
    >
      {children}
    </button>
  );
}

function formatDateTime(s: string): string {
  try {
    return new Date(s).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'medium' });
  } catch {
    return s;
  }
}

function defaultColumnWidth(column: DbAdminColumnInfo): number {
  if (column.clrType.startsWith('guid')) return 270;
  if (column.clrType.startsWith('datetime')) return 190;
  if (column.clrType.startsWith('boolean')) return 120;
  if (column.clrType.startsWith('int') || column.clrType.startsWith('decimal') || column.clrType.startsWith('double')) return 130;
  return 200;
}
