import {
  ArrowDown,
  ArrowUp,
  ArrowsVertical,
  ChevronDown,
  ChevronUp,
  Download,
  Pause,
  Play,
} from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { diagnostics, type SupportEventQuery, type SupportEventResponse } from '../../api/diagnostics';

/**
 * Structured table view of the <c>SupportEvents</c> data. Body rows are virtualized via
 * <c>@tanstack/react-virtual</c>, columns are resizable via a drag handle, and widths are
 * saved in localStorage so the layout survives a tab refresh.
 */
const PAGE_SIZE = 200;
// Compact row height: fits one line of text-xs with small top/bottom padding, giving a
// real log-viewer feel similar to Notepad++ or CMTrace.
const ROW_HEIGHT = 22;
const HEADER_HEIGHT = 26;
const DEFAULT_TABLE_HEIGHT = 702;
const MIN_TABLE_HEIGHT = 180;
// `.v2`: bumping the storage key resets the value. Without a new key, a browser with an
// existing stored height would never pick up the new default.
const HEIGHT_STORAGE_KEY = 'nodepilot.supportEvents.tableHeight.v2';

const LEVEL_LABELS: Record<number, string> = {
  0: 'TRACE', 1: 'DEBUG', 2: 'INFO', 3: 'WARN', 4: 'ERROR', 5: 'FATAL',
};

const EVENT_TYPE_OPTIONS = [
  '', 'USER_LOG',
  'EXECUTION_STARTED', 'EXECUTION_SUCCEEDED', 'EXECUTION_FAILED', 'EXECUTION_CANCELLED',
  'STEP_FAILED', 'AUDIT', 'SYSTEM_BOOT', 'MIGRATION_APPLIED',
];

type ColumnId = 'time' | 'level' | 'type' | 'status' | 'workflow' | 'exec' | 'step' | 'user' | 'message';

/**
 * Column definition. <c>sortKey</c> is the backend allowlist value; null disables sorting.
 * Status and message are not sortable because the UI derives one and the other may be large.
 */
// The message appears before metadata so operators can read it without horizontal scrolling.
// It is the only flexible column and fills the remaining width, so it is not pixel-resizable.
const MESSAGE_COLUMN_ID: ColumnId = 'message';
const COLUMNS: { id: ColumnId; label: string; defaultWidth: number; minWidth: number; sortKey: string | null }[] = [
  { id: 'time',     label: 'Zeit',             defaultWidth: 170, minWidth: 120, sortKey: 'timestamp' },
  { id: 'level',    label: 'Level',            defaultWidth: 60,  minWidth: 50,  sortKey: 'level' },
  { id: 'message',  label: 'Message',          defaultWidth: 420, minWidth: 220, sortKey: 'message' },
  { id: 'type',     label: 'Typ',              defaultWidth: 150, minWidth: 80,  sortKey: 'eventType' },
  // The backend sorts status by EventType to keep matching states together.
  { id: 'status',   label: 'Status',           defaultWidth: 90,  minWidth: 60,  sortKey: 'status' },
  { id: 'workflow', label: 'Workflow',         defaultWidth: 220, minWidth: 100, sortKey: 'workflowName' },
  { id: 'step',     label: 'Step / Activity',  defaultWidth: 220, minWidth: 100, sortKey: 'stepLabel' },
  { id: 'exec',     label: 'Exec',             defaultWidth: 90,  minWidth: 70,  sortKey: 'executionShort' },
  { id: 'user',     label: 'User',             defaultWidth: 90,  minWidth: 60,  sortKey: 'userName' },
];

const WIDTH_STORAGE_KEY = 'nodepilot.supportEvents.columnWidths.v1';

function loadColumnWidths(): Record<ColumnId, number> {
  const defaults = Object.fromEntries(COLUMNS.map((c) => [c.id, c.defaultWidth])) as Record<ColumnId, number>;
  if (typeof window === 'undefined') return defaults;
  try {
    const raw = globalThis.localStorage.getItem(WIDTH_STORAGE_KEY);
    if (!raw) return defaults;
    const parsed = JSON.parse(raw) as Partial<Record<ColumnId, number>>;
    for (const col of COLUMNS) {
      const v = parsed[col.id];
      if (typeof v === 'number' && Number.isFinite(v) && v >= col.minWidth) {
        defaults[col.id] = v;
      }
    }
  } catch { /* corrupt localStorage → defaults */ }
  return defaults;
}

export function SupportEventsTable() {
  const { t } = useTranslation(['adminSettings', 'common']);
  // Only the step label needs translation; the other labels are German or language-neutral.
  const columnLabel = useCallback(
    (col: typeof COLUMNS[number]) => (col.id === 'step' ? t('supportEvents.stepActivityColumn') : col.label),
    [t]
  );
  // Match the backend default by sorting newest first. Header clicks toggle direction or select
  // another sortable column; columns without a sort key remain inactive.
  const [filter, setFilter] = useState<SupportEventQuery>({
    take: PAGE_SIZE,
    sortBy: 'timestamp',
    sortDir: 'desc',
  });
  const [paused, setPaused] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);

  const onColumnClick = useCallback((col: typeof COLUMNS[number]) => {
    if (!col.sortKey) return;
    setFilter((f) => {
      if (f.sortBy === col.sortKey) {
        // A repeated click on the same column reverses its direction.
        return { ...f, sortDir: f.sortDir === 'desc' ? 'asc' : 'desc' };
      }
      // A newly selected column starts in descending order.
      return { ...f, sortBy: col.sortKey ?? undefined, sortDir: 'desc' };
    });
  }, []);

  const [widths, setWidths] = useState<Record<ColumnId, number>>(() => loadColumnWidths());
  useEffect(() => {
    if (typeof window === 'undefined') return;
    try { globalThis.localStorage.setItem(WIDTH_STORAGE_KEY, JSON.stringify(widths)); } catch { /* quota / private mode */ }
  }, [widths]);

  // Persist the resizable viewport height so operators can retain their preferred row count.
  const [tableHeight, setTableHeight] = useState<number>(() => {
    if (typeof window === 'undefined') return DEFAULT_TABLE_HEIGHT;
    try {
      const raw = globalThis.localStorage.getItem(HEIGHT_STORAGE_KEY);
      const n = raw ? Number.parseInt(raw, 10) : Number.NaN;
      return Number.isFinite(n) && n >= MIN_TABLE_HEIGHT ? n : DEFAULT_TABLE_HEIGHT;
    } catch { return DEFAULT_TABLE_HEIGHT; }
  });
  useEffect(() => {
    if (typeof window === 'undefined') return;
    try { globalThis.localStorage.setItem(HEIGHT_STORAGE_KEY, String(tableHeight)); } catch { /* quota */ }
  }, [tableHeight]);

  const startHeightResize = useCallback((startY: number, startHeight: number) => {
    const move = (ev: PointerEvent) => {
      const next = Math.max(MIN_TABLE_HEIGHT, startHeight + (ev.clientY - startY));
      setTableHeight(next);
    };
    const up = () => {
      globalThis.removeEventListener('pointermove', move);
      globalThis.removeEventListener('pointerup', up);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    globalThis.addEventListener('pointermove', move);
    globalThis.addEventListener('pointerup', up);
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
  }, []);

  const [searchInput, setSearchInput] = useState('');
  useEffect(() => {
    const handle = setTimeout(() => setFilter((f) => ({ ...f, q: searchInput || undefined })), 300);
    return () => clearTimeout(handle);
  }, [searchInput]);

  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['diagnostics', 'support-events', filter],
    queryFn: () => diagnostics.queryEvents(filter),
    refetchInterval: paused ? false : 5000,
    refetchOnWindowFocus: true,
  });

  const rows = data?.items ?? [];

  const virtualizer = useVirtualizer({
    count: rows.length,
    estimateSize: () => ROW_HEIGHT,
    getScrollElement: () => scrollRef.current,
    overscan: 8,
    // Measure expanded rows from the DOM because their detail block exceeds the fixed row height.
    measureElement: typeof window !== 'undefined' ? (el) => el.getBoundingClientRect().height : undefined,
  });

  const eventTypeCounts = useMemo(() => {
    const m = new Map<string, number>();
    for (const r of rows) m.set(r.eventType, (m.get(r.eventType) ?? 0) + 1);
    return Array.from(m.entries()).sort((a, b) => b[1] - a[1]).slice(0, 6);
  }, [rows]);

  // The flexible message column fills the wrapper while every other column stays resizable.
  const gridTemplate = useMemo(
    () => COLUMNS.map((c) =>
      c.id === MESSAGE_COLUMN_ID ? `minmax(${c.minWidth}px, 1fr)` : `${widths[c.id]}px`
    ).join(' '),
    [widths]
  );
  // The shared minimum width makes the header and body scroll together only when necessary.
  const minGridWidth = useMemo(
    () => COLUMNS.reduce((sum, c) =>
      sum + (c.id === MESSAGE_COLUMN_ID ? c.minWidth : widths[c.id]), 0),
    [widths]
  );

  const startResize = useCallback((id: ColumnId, startX: number, startWidth: number) => {
    const col = COLUMNS.find((c) => c.id === id);
    if (!col) return;
    const move = (ev: PointerEvent) => {
      const next = Math.max(col.minWidth, startWidth + (ev.clientX - startX));
      setWidths((w) => ({ ...w, [id]: next }));
    };
    const up = () => {
      globalThis.removeEventListener('pointermove', move);
      globalThis.removeEventListener('pointerup', up);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    globalThis.addEventListener('pointermove', move);
    globalThis.addEventListener('pointerup', up);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }, []);

  return (
    <div className="space-y-3">
      {/* Filter-Leiste */}
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <select value={filter.eventType ?? ''}
          onChange={(e) => setFilter((f) => ({ ...f, eventType: e.target.value || undefined }))}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          {EVENT_TYPE_OPTIONS.map((t) => (
            <option key={t || '__all'} value={t}>{t || 'alle Typen'}</option>
          ))}
        </select>
        <select value={filter.level ?? ''}
          onChange={(e) => setFilter((f) => ({ ...f, level: e.target.value ? Number(e.target.value) : undefined }))}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          <option value="">alle Level</option>
          <option value="2">INFO+</option>
          <option value="3">WARN+</option>
          <option value="4">ERROR+</option>
        </select>
        <input type="text" placeholder={t('adminSettings:supportEvents.filterWorkflowName')} value={filter.workflowName ?? ''}
          onChange={(e) => setFilter((f) => ({ ...f, workflowName: e.target.value || undefined }))}
          className="px-2 py-1 border border-outline-variant rounded text-xs w-44" />
        <input type="text" placeholder={t('adminSettings:supportEvents.filterUsername')} value={filter.username ?? ''}
          onChange={(e) => setFilter((f) => ({ ...f, username: e.target.value || undefined }))}
          className="px-2 py-1 border border-outline-variant rounded text-xs w-32" />
        <input type="text" placeholder={t('adminSettings:supportEvents.filterFullText')} value={searchInput}
          onChange={(e) => setSearchInput(e.target.value)}
          className="px-2 py-1 border border-outline-variant rounded text-xs w-48" />
        <button type="button" onClick={() => setPaused(!paused)}
          title={paused ? 'Polling fortsetzen' : 'Polling pausieren'}
          className="flex items-center gap-1 px-2 py-1 text-xs border border-outline-variant rounded hover:bg-surface-low">
          {paused ? <Play size={12} /> : <Pause size={12} />} {paused ? t('supportEvents.resume') : t('supportEvents.pause')}
        </button>
        <button type="button" onClick={() => refetch()}
          className="px-2 py-1 text-xs border border-outline-variant rounded hover:bg-surface-low">
          {t('common:refresh')}
        </button>
        <div className="ml-auto flex items-center gap-1">
          <button type="button" onClick={() => diagnostics.exportEvents('csv', filter)}
            className="flex items-center gap-1 px-2 py-1 text-xs border border-outline-variant rounded hover:bg-surface-low">
            <Download size={12} /> CSV
          </button>
          <button type="button" onClick={() => diagnostics.exportEvents('ndjson', filter)}
            className="flex items-center gap-1 px-2 py-1 text-xs border border-outline-variant rounded hover:bg-surface-low">
            <Download size={12} /> NDJSON
          </button>
        </div>
      </div>
      {eventTypeCounts.length > 0 && (
        <div className="flex flex-wrap gap-1.5 text-xs">
          {eventTypeCounts.map(([type, count]) => {
            const active = filter.eventType === type;
            return (
              <button key={type} type="button"
                onClick={() => setFilter((f) => ({ ...f, eventType: active ? undefined : type }))}
                className={`px-2 py-0.5 rounded border ${
                  active
                    ? 'bg-blue-600 text-white border-blue-700'
                    : 'bg-surface-lowest border-outline-variant hover:bg-surface-low'
                }`}>
                {type} <span className="opacity-60">×{count}</span>
              </button>
            );
          })}
        </div>
      )}
      <div className="text-xs text-on-surface-variant flex items-center gap-3">
        {isLoading && <span>lädt …</span>}
        {error && <span className="text-red-600 dark:text-red-400">{(error as Error).message}</span>}
        <span>{rows.length} Events</span>
        {data?.hasMore && (
          <span className="text-amber-600 dark:text-amber-400">
            … mehr verfügbar ({data.nextCursor ? 'Filter verfeinern oder Take erhöhen' : 'Cursor inaktiv bei Custom-Sort — Filter verfeinern'})
          </span>
        )}
      </div>
      <div className="border border-outline-variant rounded overflow-hidden bg-surface-lowest">
        {/* The header and body share one horizontal scroll container and
            minimum-width wrapper. */}
        <div className="overflow-x-auto">
          <div style={{ minWidth: minGridWidth }}>
        {/* Header clicks sort; edge handles resize without propagating
            the pointer event. */}
        <div className="grid items-center text-[11px] font-medium bg-surface-low text-on-surface-variant border-b border-outline-variant relative"
          style={{ height: HEADER_HEIGHT, gridTemplateColumns: gridTemplate }}>
          {COLUMNS.map((col) => {
            const sortable = col.sortKey !== null;
            const isActive = sortable && filter.sortBy === col.sortKey;
            const dir = isActive ? filter.sortDir : null;
            return (
              <div key={col.id}
                onClick={() => sortable && onColumnClick(col)}
                onKeyDown={sortable ? (e) => (e.key === 'Enter' || e.key === ' ') && onColumnClick(col) : undefined}
                role={sortable ? 'button' : undefined}
                tabIndex={sortable ? 0 : undefined}
                aria-sort={isActive ? (dir === 'asc' ? 'ascending' : 'descending') : undefined}
                className={`relative px-2 select-none truncate flex items-center gap-1 ${
                  sortable ? 'cursor-pointer hover:bg-surface-lowest hover:text-on-surface' : ''
                } ${isActive ? 'text-blue-700' : ''}`}
                title={sortable ? `Sortieren nach ${columnLabel(col)}` : columnLabel(col)}>
                <span className="truncate">{columnLabel(col)}</span>
                {sortable && (
                  isActive
                    ? (dir === 'asc' ? <ArrowUp size={9} className="shrink-0" /> : <ArrowDown size={9} className="shrink-0" />)
                    : <ArrowsVertical size={9} className="shrink-0 opacity-30" />
                )}
                {/* Fixed columns have resize handles; the flexible message column
                    does not. */}
                {col.id !== MESSAGE_COLUMN_ID && (
                  <div
                    onPointerDown={(e) => {
                      // Prevent a resize gesture from also toggling the sort direction.
                      e.preventDefault();
                      e.stopPropagation();
                      startResize(col.id, e.clientX, widths[col.id]);
                    }}
                    // Capture clicks because a near-edge press may target the outer header.
                    onClickCapture={(e) => e.stopPropagation()}
                    // Keep the hit area wide and above header text so narrow columns remain usable.
                    className="absolute top-0 -right-1 h-full w-2 cursor-col-resize hover:bg-blue-500/60 z-10"
                    title={t('adminSettings:supportEvents.resizeColumn')}
                  />
                )}
              </div>
            );
          })}
        </div>

        {/* Virtualized body. Cell order must match COLUMNS because grid columns
            are positional. */}
        <div ref={scrollRef} style={{ height: tableHeight, overflowY: 'auto', overflowX: 'hidden' }}>
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((vi) => {
              const r = rows[vi.index];
              const expanded = expandedId === r.id;
              return (
                <div key={r.id}
                  data-index={vi.index}
                  ref={virtualizer.measureElement}
                  style={{
                    position: 'absolute',
                    top: 0, left: 0, right: 0,
                    transform: `translateY(${vi.start}px)`,
                  }}>
                  <button type="button" onClick={() => setExpandedId(expanded ? null : r.id)}
                    className={`grid w-full text-left items-center text-[11px] leading-tight border-b border-outline-variant/60 hover:bg-surface-low ${rowColor(r)}`}
                    style={{ minHeight: ROW_HEIGHT, gridTemplateColumns: gridTemplate }}>
                    <div className="px-2 font-mono truncate">{formatTimestamp(r.timestamp)}</div>
                    <div className={`px-2 font-semibold ${levelColor(r.level)}`}>{LEVEL_LABELS[r.level] ?? r.level}</div>
                    <div className="px-2 truncate flex items-center gap-1" title={r.message}>
                      {expanded ? <ChevronUp size={9} className="shrink-0" /> : <ChevronDown size={9} className="shrink-0" />}
                      <span className="truncate">{r.message}</span>
                    </div>
                    <div className="px-2 font-mono truncate" title={r.eventType}>{r.eventType}</div>
                    <div className={`px-2 font-semibold truncate ${statusColor(r)}`}>{deriveStatus(r)}</div>
                    <div className="px-2 truncate" title={r.workflowName ?? ''}>{r.workflowName ?? '—'}</div>
                    <div className="px-2 truncate" title={`${r.stepLabel ?? ''} / ${r.activityType ?? ''}`}>
                      {r.stepLabel ?? '—'}
                      {r.activityType && <span className="text-on-surface-variant ml-1">({r.activityType})</span>}
                    </div>
                    <div className="px-2 font-mono text-on-surface-variant truncate">{r.executionShort ?? ''}</div>
                    <div className="px-2 truncate text-on-surface-variant">{r.userName ?? ''}</div>
                  </button>
                  {expanded && <EventDetail event={r} />}
                </div>
              );
            })}
            {rows.length === 0 && !isLoading && (
              <div className="text-center text-on-surface-variant text-sm py-8">— keine Events —</div>
            )}
          </div>
        </div>
          </div>
        </div>

        {/* Dragging the bottom handle resizes
            the viewport. */}
        <div
          onPointerDown={(e) => {
            e.preventDefault();
            startHeightResize(e.clientY, tableHeight);
          }}
          className="h-1.5 bg-surface-low hover:bg-blue-500/40 cursor-row-resize border-t border-outline-variant flex items-center justify-center group"
          title={t('adminSettings:supportEvents.resizeTableHeight')}
        >
          <div className="w-12 h-0.5 bg-on-surface-variant/30 group-hover:bg-blue-600/60 rounded-full" />
        </div>
      </div>
    </div>
  );
}

function EventDetail({ event }: Readonly<{ event: SupportEventResponse }>) {
  let props: Record<string, unknown> | null = null;
  if (event.propertiesJson) {
    try { props = JSON.parse(event.propertiesJson) as Record<string, unknown>; }
    catch { /* propertiesJson kann truncated sein, fallback rendert den raw String */ }
  }
  return (
    <div className="border-l-2 border-blue-500 border-b border-outline-variant bg-surface-low px-4 py-3 text-xs space-y-2">
      <div><span className="font-semibold text-on-surface">Message:</span> <span className="break-all text-on-surface">{event.message}</span></div>
      <div className="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-1 text-on-surface-variant">
        {event.workflowId && <div><span>WorkflowId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.workflowId}</code></div>}
        {event.executionId && <div><span>ExecutionId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.executionId}</code></div>}
        {event.stepId && <div><span>StepId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.stepId}</code></div>}
        {event.userId && <div><span>UserId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.userId}</code></div>}
        {event.traceId && <div><span>TraceId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.traceId}</code></div>}
        {event.spanId && <div><span>SpanId:</span> <code className="text-on-surface bg-surface px-1 rounded">{event.spanId}</code></div>}
      </div>
      {props && (
        <div>
          <div className="text-on-surface-variant mb-1">Properties:</div>
          <pre className="bg-surface border border-outline-variant text-on-surface p-2 rounded font-mono text-[11px] overflow-auto max-h-48">{JSON.stringify(props, null, 2)}</pre>
        </div>
      )}
      {!props && event.propertiesJson && (
        <div>
          <div className="text-on-surface-variant mb-1">Properties (raw):</div>
          <pre className="bg-surface border border-outline-variant text-on-surface p-2 rounded font-mono text-[11px] overflow-auto max-h-48">{event.propertiesJson}</pre>
        </div>
      )}
    </div>
  );
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toISOString().replace('T', ' ').slice(0, 23);
}

function levelColor(level: number): string {
  if (level >= 4) return 'text-red-600 dark:text-red-400';
  if (level >= 3) return 'text-amber-600 dark:text-amber-400';
  return 'text-on-surface-variant';
}

/**
 * Derives status from <c>EventType</c> because a separate database field would duplicate it.
 * Events without an outcome display a dash.
 */
function deriveStatus(r: SupportEventResponse): string {
  switch (r.eventType) {
    case 'EXECUTION_SUCCEEDED': return 'Succeeded';
    case 'EXECUTION_FAILED':    return 'Failed';
    case 'EXECUTION_CANCELLED': return 'Cancelled';
    case 'STEP_FAILED':         return 'Failed';
    case 'EXECUTION_STARTED':   return 'Running';
    case 'SYSTEM_BOOT':         return 'Started';
    case 'MIGRATION_APPLIED':   return 'Applied';
  }
  // Audit events use their level: failures are warnings or higher, while successes are information.
  if (r.eventType === 'AUDIT') return r.level >= 3 ? 'Failed' : 'OK';
  return '—';
}

function statusColor(r: SupportEventResponse): string {
  const s = deriveStatus(r);
  if (s === 'Succeeded' || s === 'OK' || s === 'Started' || s === 'Applied') return 'text-green-600 dark:text-green-400';
  if (s === 'Failed') return 'text-red-600 dark:text-red-400';
  if (s === 'Cancelled') return 'text-amber-600 dark:text-amber-400';
  if (s === 'Running') return 'text-sky-600 dark:text-sky-400';
  return 'text-on-surface-variant';
}

// A low-opacity semantic tint remains visible in both light and dark themes.
// Failures use a stronger tint than successes.
function rowColor(r: SupportEventResponse): string {
  if (r.level >= 4) return 'bg-red-500/5 dark:bg-red-500/10';
  if (r.eventType === 'STEP_FAILED' || r.eventType === 'EXECUTION_FAILED') return 'bg-red-500/5 dark:bg-red-500/[0.08]';
  if (r.eventType === 'EXECUTION_SUCCEEDED') return 'bg-green-500/5 dark:bg-green-500/[0.05]';
  if (r.eventType === 'AUDIT') return 'bg-violet-500/5 dark:bg-violet-500/10';
  return '';
}
