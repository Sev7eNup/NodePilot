import {
  ArrowDownRight,
  ChevronDown,
  ChevronRight,
  ChevronUp,
  CircleDash,
  DataBase,
  Launch,
  Renew,
  Reset,
  Search,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { api } from '../api/client';
import type { WorkflowExecution, StepExecution, Workflow } from '../types/api';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { buildTraceUrl, useObservabilityConfig } from '../api/observability';
import { formatDate, formatDuration, formatRelative } from '../lib/format';
import { parseOutputParametersJson } from '../lib/outputParameters';
import { useRole } from '../lib/rbac';
import { useIsMobile } from '../hooks/useMediaQuery';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import {
  ExecutionStatusBadge, TriggerCell,
} from '../components/designer/execution/ExecutionPanelParts';

// Status buckets surfaced as quick-filter chips. The list endpoint is terminalOnly,
// so these three cover every row the page can ever render.
type StatusFilter = 'all' | 'Succeeded' | 'Failed' | 'Cancelled';

// ColKey covers every sortable column; ResizableColKey drops the auto-flex "workflow"
// column (no explicit width / no drag-handle) — same pattern as WorkflowsPage / GlobalVariablesPage.
type ColKey = 'status' | 'workflow' | 'trigger' | 'startedBy' | 'steps' | 'duration' | 'started';
type ResizableColKey = Exclude<ColKey, 'workflow'>;

const WORKFLOW_MIN_WIDTH = 240;
const ACTIONS_WIDTH = 120; // ≤3 icon buttons × ~30px + gap + px-4 cell padding
const DEFAULT_WIDTHS: Record<ResizableColKey, number> = {
  status: 140, trigger: 160, startedBy: 140, steps: 150, duration: 100, started: 180,
};
// Per-column resize minimum. Status must be wide enough to hold the German worst-case
// "Fehlgeschlagen" badge, otherwise the nowrap pill clips again once you shrink the column.
// Everything else falls back to the global floor of 60.
const MIN_WIDTHS: Partial<Record<ResizableColKey, number>> = { status: 140 };

function executionDurationMs(e: WorkflowExecution): number | null {
  if (!e.completedAt) return null;
  return new Date(e.completedAt).getTime() - new Date(e.startedAt).getTime();
}

export function ExecutionsPage() {
  const { t } = useTranslation(['executions', 'common']);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { canWrite } = useRole();
  const isMobile = useIsMobile();
  const [searchParams] = useSearchParams();
  // Dashboard's "Recent runs" navigates here with `?id=<execId>` so the matching row
  // expands and scrolls into view. Initial render reads the param once; manual toggling
  // afterwards still works because expandedId is plain state.
  const [expandedId, setExpandedId] = useState<string | null>(() => searchParams.get('id'));
  const initialIdRef = useRef<string | null>(searchParams.get('id'));

  const [search, setSearch] = useState('');
  // Deep-link: the dashboard's "Show failed" shortcut navigates here with ?status=Failed.
  // Read once on mount so the list lands on the matching filter instead of showing all.
  const statusParam = searchParams.get('status');
  const initialStatus: StatusFilter =
    statusParam === 'Succeeded' || statusParam === 'Failed' || statusParam === 'Cancelled'
      ? statusParam : 'all';
  const [statusFilter, setStatusFilter] = useState<StatusFilter>(initialStatus);
  const [workflowFilter, setWorkflowFilter] = useState<string>('all');
  const [sortBy, setSortBy] = useState<ColKey>('started');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');

  // The global executions list shows only finished runs (Succeeded / Failed / Cancelled).
  // Running runs belong in the designer's live panel, not in this history list.
  // No refetchInterval: SignalR's ExecutionStatusChanged invalidates this cache
  // specifically (see useSignalR.scheduleQueryInvalidate). Every real status change
  // triggers a debounced refetch — no more 5s polling against a possibly idle server.
  const { data: executions, isLoading, isFetching, refetch } = useQuery({
    queryKey: ['executions', 'terminalOnly'],
    queryFn: () => api.get<WorkflowExecution[]>('/executions?terminalOnly=true'),
  });

  const { data: workflows } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
  });

  const { data: observability } = useObservabilityConfig();

  const workflowNames = useMemo(
    () => new Map(workflows?.map((w) => [w.id, w.name]) ?? []),
    [workflows],
  );
  const workflowNameOf = (e: WorkflowExecution) =>
    workflowNames.get(e.workflowId) ?? e.workflowId.slice(0, 8) + '…';

  const retryMutation = useMutation({
    mutationFn: (id: string) => api.post(`/executions/${id}/retry`, {}),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['executions'] }),
    onError: (err: Error) => toast.error(t('executions:retryFailed', { message: err.message })),
  });

  // --- Column resizing (mirrors WorkflowsPage / GlobalVariablesPage) ---
  const [colWidths, setColWidths] = useState(DEFAULT_WIDTHS);
  const resizeRef = useRef<{ col: ResizableColKey; startX: number; startWidth: number } | null>(null);
  const startResize = (col: ResizableColKey, e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    resizeRef.current = { col, startX: e.clientX, startWidth: colWidths[col] };
    const onMove = (ev: MouseEvent) => {
      if (!resizeRef.current) return;
      const { col, startWidth, startX } = resizeRef.current;
      const w = Math.max(MIN_WIDTHS[col] ?? 60, startWidth + ev.clientX - startX);
      setColWidths((prev) => ({ ...prev, [col]: w }));
    };
    const onUp = () => {
      resizeRef.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  };
  const tableMinWidth = useMemo(
    () => Object.values(colWidths).reduce((a, b) => a + b, 0) + ACTIONS_WIDTH + WORKFLOW_MIN_WIDTH,
    [colWidths],
  );

  const handleSort = (col: ColKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    // Time-ish columns feel more natural newest-first on the first click.
    else { setSortBy(col); setSortDir(col === 'started' || col === 'duration' ? 'desc' : 'asc'); }
  };

  // Summary over the FULL (unfiltered) result set so the headline numbers don't shift
  // as the user narrows the table.
  const summary = useMemo(() => {
    const list = executions ?? [];
    const succeeded = list.filter((e) => e.status === 'Succeeded').length;
    const failed = list.filter((e) => e.status === 'Failed').length;
    const cancelled = list.filter((e) => e.status === 'Cancelled').length;
    const durations = list.map(executionDurationMs).filter((d): d is number => d != null && d >= 0);
    const avg = durations.length ? durations.reduce((a, b) => a + b, 0) / durations.length : null;
    const rateBase = succeeded + failed; // cancelled runs aren't a quality signal
    const successRate = rateBase > 0 ? Math.round((succeeded / rateBase) * 100) : null;
    return { total: list.length, succeeded, failed, cancelled, avg, successRate };
  }, [executions]);

  const filteredSorted = useMemo(() => {
    let list = executions ?? [];
    if (statusFilter !== 'all') list = list.filter((e) => e.status === statusFilter);
    if (workflowFilter !== 'all') list = list.filter((e) => e.workflowId === workflowFilter);
    const term = search.trim().toLowerCase();
    if (term) {
      list = list.filter((e) =>
        workflowNameOf(e).toLowerCase().includes(term)
        || (e.triggeredBy ?? '').toLowerCase().includes(term)
        || (e.startedByUsername ?? '').toLowerCase().includes(term)
        || e.id.toLowerCase().includes(term)
        || (e.errorMessage ?? '').toLowerCase().includes(term),
      );
    }
    const sorted = [...list].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'status':    cmp = a.status.localeCompare(b.status); break;
        case 'workflow':  cmp = workflowNameOf(a).localeCompare(workflowNameOf(b)); break;
        case 'trigger':   cmp = (a.triggeredBy ?? '').localeCompare(b.triggeredBy ?? ''); break;
        case 'startedBy': cmp = (a.startedByUsername ?? '').localeCompare(b.startedByUsername ?? ''); break;
        case 'steps':     cmp = (a.stepsCompleted ?? 0) - (b.stepsCompleted ?? 0); break;
        case 'duration':  cmp = (executionDurationMs(a) ?? -1) - (executionDurationMs(b) ?? -1); break;
        case 'started':   cmp = a.startedAt.localeCompare(b.startedAt); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
    return sorted;
    // workflowNameOf depends on workflowNames; listing it keeps the sort stable across renames.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [executions, statusFilter, workflowFilter, search, sortBy, sortDir, workflowNames]);

  // --- Virtual scrolling (desktop only) ---
  const listRef = useRef<HTMLDivElement>(null);
  const [scrollMargin, setScrollMargin] = useState(0);

  useEffect(() => {
    if (isMobile) return;
    const scroll = document.getElementById('np-main-scroll');
    const list = listRef.current;
    if (!scroll || !list) return;
    setScrollMargin(
      Math.round(list.getBoundingClientRect().top - scroll.getBoundingClientRect().top + scroll.scrollTop)
    );
   
  }, [isLoading, isMobile]);

  const gridTemplate = useMemo(
    () => [
      `${colWidths.status}px`,
      `minmax(${WORKFLOW_MIN_WIDTH}px, 1fr)`,
      `${colWidths.trigger}px`,
      `${colWidths.startedBy}px`,
      `${colWidths.steps}px`,
      `${colWidths.duration}px`,
      `${colWidths.started}px`,
      `${ACTIONS_WIDTH}px`,
    ].join(' '),
    [colWidths],
  );

  const rowVirtualizer = useVirtualizer({
    count: isMobile ? 0 : filteredSorted.length,
    getScrollElement: () => document.getElementById('np-main-scroll'),
    estimateSize: (index) => filteredSorted[index]?.id === expandedId ? 300 : 52,
    overscan: 8,
    getItemKey: (index) => filteredSorted[index].id,
    measureElement: (el) => el?.getBoundingClientRect().height ?? 52,
    scrollMargin,
    // jsdom has no layout → initialRect prevents 0-item render in unit tests.
    initialRect: { width: 1200, height: 900 },
  });

  // Once the executions list is loaded, scroll the deep-linked row into view. Uses
  // virtualizer.scrollToIndex so the target is always within the virtual window.
  const scrolledForRef = useRef<string | null>(null);
  useEffect(() => {
    const target = initialIdRef.current;
    if (!target || isLoading || !executions) return;
    if (scrolledForRef.current === target) return;
    if (!executions.some((e) => e.id === target)) return;
    scrolledForRef.current = target;
    const idx = filteredSorted.findIndex((e) => e.id === target);
    if (idx >= 0) rowVirtualizer.scrollToIndex(idx, { align: 'center', behavior: 'smooth' });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [executions, isLoading]);

  const sortIcon = (col: ColKey) =>
    sortBy === col ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />) : <span className="w-3" />;

  const totalCount = executions?.length ?? 0;

  return (
    <div className="max-w-[1600px] mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-4">
        <div>
          <p className="text-sm text-on-surface-variant">{t('executions:subtitle')}</p>
        </div>
        <button
          onClick={() => refetch()}
          className={`flex items-center gap-2 px-3 py-2 bg-surface-lowest border border-outline-variant text-on-surface rounded-md hover:bg-surface-low transition-colors text-sm ${isFetching ? 'opacity-60' : ''}`}
          title={t('common:reload')}
        >
          <Renew size={16} className={isFetching ? 'animate-spin' : ''} /> <span className="hidden sm:inline">{t('common:refresh')}</span>
        </button>
      </div>
      {/* Summary chips — quick read on the whole result set. */}
      {totalCount > 0 && (
        <div className="np-card p-3 mb-3 flex flex-wrap items-center gap-x-6 gap-y-2 text-sm">
          <SummaryStat label={t('executions:summary.total')} value={summary.total} />
          <SummaryStat label={t('executions:summary.succeeded')} value={summary.succeeded} tone="green" />
          <SummaryStat label={t('executions:summary.failed')} value={summary.failed} tone={summary.failed > 0 ? 'red' : undefined} />
          <SummaryStat label={t('executions:summary.cancelled')} value={summary.cancelled} tone={summary.cancelled > 0 ? 'amber' : undefined} />
          <SummaryStat
            label={t('executions:summary.successRate')}
            value={summary.successRate == null ? t('common:dash') : `${summary.successRate}%`}
            tone={summary.successRate == null ? undefined : summary.successRate >= 95 ? 'green' : summary.successRate >= 80 ? 'amber' : 'red'}
          />
          <SummaryStat label={t('executions:summary.avgDuration')} value={formatDuration(summary.avg)} />
        </div>
      )}
      {/* Toolbar: search + status chips + workflow dropdown. */}
      {totalCount > 0 && (
        <div className="np-card p-3 mb-3 flex flex-wrap items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('executions:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>

          <div className="flex items-center gap-1">
            {(['all', 'Succeeded', 'Failed', 'Cancelled'] as StatusFilter[]).map((s) => {
              const labelKey = s === 'all' ? 'all' : s.toLowerCase();
              const active = statusFilter === s;
              return (
                <button
                  key={s}
                  onClick={() => setStatusFilter(s)}
                  className={`px-2.5 py-1 rounded-md text-xs font-medium transition-colors ${
                    active ? 'bg-primary text-on-primary' : 'bg-surface-high text-on-surface-variant hover:bg-surface-highest'
                  }`}
                >
                  {t(`executions:filters.${labelKey}`)}
                </button>
              );
            })}
          </div>

          <select
            value={workflowFilter}
            onChange={(e) => setWorkflowFilter(e.target.value)}
            className="px-2 py-1.5 border border-outline-variant rounded-md text-sm bg-surface-lowest text-on-surface focus:outline-none focus:ring-2 focus:ring-blue-500 max-w-[220px]"
          >
            <option value="all">{t('executions:allWorkflows')}</option>
            {workflows?.map((w) => (
              <option key={w.id} value={w.id}>{w.name}</option>
            ))}
          </select>
        </div>
      )}
      {isLoading ? (
        <p className="text-on-surface-variant font-label">{t('common:loadingDots')}</p>
      ) : totalCount === 0 ? (
        <div className="np-card p-8 text-center text-on-surface-variant font-label">
          {t('executions:noExecutions')}
        </div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-on-surface-variant font-label">
          {t('executions:noMatch')}
        </div>
      ) : isMobile ? (
        <div className="space-y-2" data-testid="mobile-card-list">
          {filteredSorted.map((exec) => (
            <ExecutionCard
              key={exec.id}
              execution={exec}
              workflowName={workflowNameOf(exec)}
              isExpanded={expandedId === exec.id}
              onToggle={() => setExpandedId(expandedId === exec.id ? null : exec.id)}
              onOpenWorkflow={() => navigate(`/workflows/${exec.workflowId}`)}
              onRetry={canWrite ? async () => {
                if (await confirmDialog(t('executions:retryConfirm'))) retryMutation.mutate(exec.id);
              } : undefined}
              retryPending={retryMutation.isPending}
              traceUrlTemplate={observability?.traceUiUrlTemplate ?? null}
              traceBackendName={observability?.traceBackendName ?? null}
            />
          ))}
        </div>
      ) : (
        <div className="np-card overflow-hidden"><div className="overflow-x-auto">
          <div role="table" style={{ minWidth: tableMinWidth }}>
            {/* Header row */}
            <div role="rowgroup">
              <div
                role="row"
                className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant"
                style={{ display: 'grid', gridTemplateColumns: gridTemplate }}
              >
                <SortableHeaderCell col="status" label={t('executions:tableHeaders.status')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                {/* Workflow = auto-flex column: no resize handle. */}
                <div role="columnheader" className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                  <button onClick={() => handleSort('workflow')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('executions:tableHeaders.workflow')} {sortIcon('workflow')}
                  </button>
                </div>
                <SortableHeaderCell col="trigger" label={t('executions:tableHeaders.trigger')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                <SortableHeaderCell col="startedBy" label={t('executions:tableHeaders.startedBy')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                <SortableHeaderCell col="steps" label={t('executions:tableHeaders.steps')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                <SortableHeaderCell col="duration" label={t('executions:tableHeaders.duration')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                <SortableHeaderCell col="started" label={t('executions:tableHeaders.started')} sortIcon={sortIcon} onSort={handleSort} onResize={startResize} />
                <div role="columnheader" className="px-4 py-2">{t('executions:tableHeaders.actions')}</div>
              </div>
            </div>
            {/* Virtual body — absolute-positioned items, each containing main row + optional detail. */}
            <div ref={listRef} role="rowgroup" style={{ position: 'relative', height: rowVirtualizer.getTotalSize() - scrollMargin }}>
              {rowVirtualizer.getVirtualItems().map((vRow) => {
                const exec = filteredSorted[vRow.index];
                return (
                  <div
                    key={exec.id}
                    data-index={vRow.index}
                    ref={rowVirtualizer.measureElement}
                    style={{ position: 'absolute', top: 0, left: 0, width: '100%', transform: `translateY(${vRow.start - scrollMargin}px)` }}
                  >
                    <ExecutionRow
                      execution={exec}
                      workflowName={workflowNameOf(exec)}
                      gridTemplate={gridTemplate}
                      isExpanded={expandedId === exec.id}
                      onToggle={() => setExpandedId(expandedId === exec.id ? null : exec.id)}
                      onOpenWorkflow={() => navigate(`/workflows/${exec.workflowId}`)}
                      onRetry={canWrite ? async () => {
                        if (await confirmDialog(t('executions:retryConfirm'))) retryMutation.mutate(exec.id);
                      } : undefined}
                      retryPending={retryMutation.isPending}
                      traceUrlTemplate={observability?.traceUiUrlTemplate ?? null}
                      traceBackendName={observability?.traceBackendName ?? null}
                    />
                  </div>
                );
              })}
            </div>
          </div>
        </div></div>
      )}
      {filteredSorted.length > 0 && (
        <p className="font-label text-[11px] text-on-surface-variant mt-3">
          {t('executions:showing', { count: filteredSorted.length, total: totalCount })}
        </p>
      )}
    </div>
  );
}

function SummaryStat({ label, value, tone }: Readonly<{ label: string; value: number | string; tone?: 'green' | 'red' | 'amber' }>) {
  const color =
    tone === 'green' ? 'text-green-600 dark:text-green-400' :
    tone === 'red' ? 'text-red-600 dark:text-red-400' :
    tone === 'amber' ? 'text-amber-600 dark:text-amber-400' :
    'text-on-surface';
  return (
    <span className="inline-flex items-baseline gap-1.5">
      <span className={`font-semibold tabular-nums ${color}`}>{value}</span>
      <span className="text-on-surface-variant text-xs">{label}</span>
    </span>
  );
}

function SortableHeaderCell({ col, label, sortIcon, onSort, onResize }: Readonly<{
  col: ResizableColKey;
  label: string;
  sortIcon: (col: ColKey) => React.ReactNode;
  onSort: (col: ColKey) => void;
  onResize: (col: ResizableColKey, e: React.MouseEvent) => void;
}>) {
  return (
    <div role="columnheader" className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
      <button onClick={() => onSort(col)} className="flex items-center gap-1 hover:text-on-surface transition-colors">
        {label} {sortIcon(col)}
      </button>
      <div
        onMouseDown={(e) => onResize(col, e)}
        className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
      />
    </div>
  );
}

function ExecutionRow({ execution, workflowName, gridTemplate, isExpanded, onToggle, onOpenWorkflow, onRetry, retryPending, traceUrlTemplate, traceBackendName }: Readonly<{
  execution: WorkflowExecution;
  workflowName: string;
  gridTemplate: string;
  isExpanded: boolean;
  onToggle: () => void;
  onOpenWorkflow: () => void;
  onRetry?: () => void;
  retryPending: boolean;
  traceUrlTemplate: string | null;
  traceBackendName: string | null;
}>) {
  const { t } = useTranslation(['executions', 'common']);
  const traceUrl = buildTraceUrl(traceUrlTemplate, execution.traceId);
  const { data: steps } = useQuery({
    queryKey: ['execution-steps', execution.id],
    queryFn: () => api.get<StepExecution[]>(`/executions/${execution.id}/steps`),
    enabled: isExpanded,
  });

  const durationMs = executionDurationMs(execution);
  const stepsTotal = execution.stepsTotal ?? 0;
  const stepsCompleted = execution.stepsCompleted ?? 0;
  const stepsPct = stepsTotal > 0 ? Math.round((stepsCompleted / stepsTotal) * 100) : 0;
  const failedCount = execution.failedSteps?.length ?? 0;
  const failedLabel = (execution.failedSteps ?? []).map((s) => s.stepName ?? s.stepId).join(', ');

  // Row toggle: clicking anywhere on the row expands, except on real interactive
  // elements (buttons / links) which carry their own handlers (WorkflowsPage pattern).
  const onRowClick = (e: React.MouseEvent) => {
    if (e.target instanceof Element && e.target.closest('button, a')) return;
    onToggle();
  };

  return (
    <div className="border-b border-outline-variant/30">
      {/* Main row — CSS grid mirrors the header's gridTemplate. */}
      <div
        role="row"
        onClick={onRowClick}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onToggle(); } }}
        tabIndex={0}
        className={`cursor-pointer hover:bg-surface-low transition-colors`}
        style={{ display: 'grid', gridTemplateColumns: gridTemplate, background: isExpanded ? 'var(--np-surface-low)' : undefined }}
      >
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <ExecutionStatusBadge status={execution.status} />
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <div className="flex items-center gap-2 min-w-0">
            {/* The toggle button — its accessible name carries the workflow name. */}
            <button
              onClick={onToggle}
              className="flex items-center gap-1.5 min-w-0 text-left group"
            >
              {isExpanded
                ? <ChevronDown size={14} className="text-on-surface-variant shrink-0" />
                : <ChevronRight size={14} className="text-on-surface-variant shrink-0" />}
              <span className="font-label text-sm font-medium text-on-surface truncate group-hover:text-primary transition-colors">
                {workflowName}
              </span>
            </button>
            {execution.parentExecutionId && (
              <span
                className="inline-flex items-center gap-0.5 shrink-0 text-[10px] font-semibold text-indigo-700 dark:text-indigo-400 bg-indigo-100 dark:bg-indigo-900/40 rounded px-1.5 py-0.5"
                title={execution.parentWorkflowName
                  ? t('executions:subWorkflowFrom', { parent: execution.parentWorkflowName })
                  : t('executions:subWorkflowGeneric')}
              >
                <ArrowDownRight size={10} /> {execution.parentWorkflowName ?? t('executions:subWorkflow')}
              </span>
            )}
            <button
              onClick={(e) => { e.stopPropagation(); navigator.clipboard?.writeText(execution.id).catch(() => {}); }}
              className="shrink-0 font-mono text-[10px] text-outline hover:text-primary px-1 py-0.5 rounded hover:bg-surface-high transition-colors"
              title={t('executions:copyId', { id: execution.id })}
            >
              {execution.id.slice(0, 8)}
            </button>
          </div>
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <TriggerCell triggeredBy={execution.triggeredBy} />
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <span className="text-sm text-on-surface-variant truncate block" title={execution.startedByUsername ?? ''}>
            {execution.startedByUsername ?? <span className="text-outline">{t('common:dash')}</span>}
          </span>
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          {stepsTotal > 0 ? (
            <div className="flex flex-col gap-1" title={t('executions:stepsProgressTitle', { completed: stepsCompleted, total: stepsTotal })}>
              <div className="flex items-center gap-2">
                <div className="w-14 h-1.5 bg-surface-container rounded-full overflow-hidden">
                  <div
                    className={`np-bar h-full ${failedCount > 0 ? 'np-bar-bad' : 'np-bar-ok'}`}
                    style={{ width: `${stepsPct}%` }}
                  />
                </div>
                <span className="text-xs text-on-surface-variant tabular-nums">{stepsCompleted}/{stepsTotal}</span>
              </div>
              {failedCount > 0 && (
                <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-red-600 dark:text-red-400 truncate" title={failedLabel}>
                  <WarningAltFilled size={10} /> {t('executions:stepsFailed', { count: failedCount })}
                </span>
              )}
            </div>
          ) : (
            <span className="text-outline text-xs">{t('common:dash')}</span>
          )}
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden text-sm text-on-surface-variant tabular-nums">
          {formatDuration(durationMs)}
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <span className="text-xs text-on-surface-variant block" title={formatDate(execution.startedAt)}>
            {formatRelative(execution.startedAt)}
          </span>
        </div>
        <div role="gridcell" className="px-4 py-2 overflow-hidden">
          <div className="flex items-center gap-1 whitespace-nowrap">
            {traceUrl && (
              <a
                href={traceUrl}
                target="_blank"
                rel="noopener noreferrer"
                onClick={(e) => e.stopPropagation()}
                className="p-1.5 text-amber-600 hover:bg-amber-500/15 rounded-lg"
                title={t('executions:openInTracing', { backend: traceBackendName ?? t('executions:tracingFallback') })}
              >
                <Launch size={15} />
              </a>
            )}
            {onRetry && (
              <button
                onClick={(e) => { e.stopPropagation(); onRetry(); }}
                disabled={retryPending}
                className="p-1.5 text-indigo-600 hover:bg-indigo-500/15 rounded-lg disabled:opacity-40"
                title={t('executions:retryExecution')}
              >
                {retryPending ? <CircleDash size={15} className="animate-spin" /> : <Reset size={15} />}
              </button>
            )}
            <button
              onClick={(e) => { e.stopPropagation(); onOpenWorkflow(); }}
              className="p-1.5 text-blue-600 hover:bg-blue-500/15 rounded-lg"
              title={t('executions:openWorkflow')}
            >
              <Launch size={15} />
            </button>
          </div>
        </div>
      </div>
      {/* Expanded detail — within the same virtual item so measureElement captures both. */}
      {isExpanded && (
        <ExecutionDetail
          execution={execution}
          steps={steps}
          traceUrl={traceUrl}
          traceBackendName={traceBackendName}
        />
      )}
    </div>
  );
}

/**
 * Mobile/phone card mirror of ExecutionRow. Same data + handlers, stacked into a
 * card instead of a fixed-width table row. Carries its own steps query (enabled
 * on expand) and reuses ExecutionDetail for the expanded view.
 */
function ExecutionCard({ execution, workflowName, isExpanded, onToggle, onOpenWorkflow, onRetry, retryPending, traceUrlTemplate, traceBackendName }: Readonly<{
  execution: WorkflowExecution;
  workflowName: string;
  isExpanded: boolean;
  onToggle: () => void;
  onOpenWorkflow: () => void;
  onRetry?: () => void;
  retryPending: boolean;
  traceUrlTemplate: string | null;
  traceBackendName: string | null;
}>) {
  const { t } = useTranslation(['executions', 'common']);
  const traceUrl = buildTraceUrl(traceUrlTemplate, execution.traceId);
  const { data: steps } = useQuery({
    queryKey: ['execution-steps', execution.id],
    queryFn: () => api.get<StepExecution[]>(`/executions/${execution.id}/steps`),
    enabled: isExpanded,
  });

  const durationMs = executionDurationMs(execution);
  const stepsTotal = execution.stepsTotal ?? 0;
  const stepsCompleted = execution.stepsCompleted ?? 0;
  const stepsPct = stepsTotal > 0 ? Math.round((stepsCompleted / stepsTotal) * 100) : 0;
  const failedCount = execution.failedSteps?.length ?? 0;

  return (
    <div id={`execution-row-${execution.id}`} className="np-card p-3 space-y-2">
      <div className="flex items-start justify-between gap-2 min-w-0">
        <button onClick={onToggle} className="flex items-center gap-1.5 min-w-0 text-left group">
          {isExpanded
            ? <ChevronDown size={14} className="text-on-surface-variant shrink-0" />
            : <ChevronRight size={14} className="text-on-surface-variant shrink-0" />}
          <span className="font-label text-sm font-semibold text-on-surface truncate group-hover:text-primary transition-colors">
            {workflowName}
          </span>
        </button>
        <ExecutionStatusBadge status={execution.status} />
      </div>
      {execution.parentExecutionId && (
        <span
          className="inline-flex items-center gap-0.5 text-[10px] font-semibold text-indigo-700 dark:text-indigo-400 bg-indigo-100 dark:bg-indigo-900/40 rounded px-1.5 py-0.5"
          title={execution.parentWorkflowName ? t('executions:subWorkflowFrom', { parent: execution.parentWorkflowName }) : t('executions:subWorkflowGeneric')}
        >
          <ArrowDownRight size={10} /> {execution.parentWorkflowName ?? t('executions:subWorkflow')}
        </span>
      )}
      <dl className="grid grid-cols-[minmax(0,auto)_1fr] gap-x-3 gap-y-1 text-sm">
        <dt className="text-xs text-on-surface-variant/70 pt-0.5">{t('executions:tableHeaders.trigger')}</dt>
        <dd className="min-w-0"><TriggerCell triggeredBy={execution.triggeredBy} /></dd>

        <dt className="text-xs text-on-surface-variant/70 pt-0.5">{t('executions:tableHeaders.startedBy')}</dt>
        <dd className="min-w-0 text-on-surface-variant">{execution.startedByUsername ?? t('common:dash')}</dd>

        <dt className="text-xs text-on-surface-variant/70 pt-0.5">{t('executions:tableHeaders.steps')}</dt>
        <dd className="min-w-0">
          {stepsTotal > 0 ? (
            <div className="flex items-center gap-2">
              <div className="w-14 h-1.5 bg-surface-container rounded-full overflow-hidden">
                <div className={`np-bar h-full ${failedCount > 0 ? 'np-bar-bad' : 'np-bar-ok'}`} style={{ width: `${stepsPct}%` }} />
              </div>
              <span className="text-xs text-on-surface-variant tabular-nums">{stepsCompleted}/{stepsTotal}</span>
              {failedCount > 0 && (
                <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-red-600 dark:text-red-400">
                  <WarningAltFilled size={10} /> {t('executions:stepsFailed', { count: failedCount })}
                </span>
              )}
            </div>
          ) : <span className="text-outline text-xs">{t('common:dash')}</span>}
        </dd>

        <dt className="text-xs text-on-surface-variant/70 pt-0.5">{t('executions:tableHeaders.duration')}</dt>
        <dd className="min-w-0 text-on-surface-variant tabular-nums">{formatDuration(durationMs)}</dd>

        <dt className="text-xs text-on-surface-variant/70 pt-0.5">{t('executions:tableHeaders.started')}</dt>
        <dd className="min-w-0 text-on-surface-variant" title={formatDate(execution.startedAt)}>{formatRelative(execution.startedAt)}</dd>
      </dl>
      <div className="flex items-center justify-end gap-1 pt-2 border-t border-outline/30">
        {traceUrl && (
          <a href={traceUrl} target="_blank" rel="noopener noreferrer" className="p-2 text-amber-600 hover:bg-amber-500/15 rounded-lg"
             title={t('executions:openInTracing', { backend: traceBackendName ?? t('executions:tracingFallback') })}>
            <Launch size={15} />
          </a>
        )}
        {onRetry && (
          <button onClick={onRetry} disabled={retryPending} className="p-2 text-indigo-600 hover:bg-indigo-500/15 rounded-lg disabled:opacity-40" title={t('executions:retryExecution')}>
            {retryPending ? <CircleDash size={15} className="animate-spin" /> : <Reset size={15} />}
          </button>
        )}
        <button onClick={onOpenWorkflow} className="p-2 text-blue-600 hover:bg-blue-500/15 rounded-lg" title={t('executions:openWorkflow')}>
          <Launch size={15} />
        </button>
      </div>
      {isExpanded && (
        <div className="-mx-3 -mb-3">
          <ExecutionDetail execution={execution} steps={steps} traceUrl={traceUrl} traceBackendName={traceBackendName} />
        </div>
      )}
    </div>
  );
}

function ExecutionDetail({ execution, steps, traceUrl, traceBackendName }: Readonly<{
  execution: WorkflowExecution;
  steps: StepExecution[] | undefined;
  traceUrl: string | null;
  traceBackendName: string | null;
}>) {
  const { t } = useTranslation(['executions']);
  const statusColors: Record<string, string> = {
    Succeeded: 'bg-green-500/15 text-green-600 dark:text-green-400',
    Failed: 'bg-red-500/15 text-red-600 dark:text-red-400',
    Running: 'bg-blue-500/15 text-blue-600 dark:text-blue-400',
    Pending: 'bg-surface-high text-on-surface-variant',
    Cancelled: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    Skipped: 'bg-surface-container text-on-surface-variant',
  };

  return (
    <div className="border-t border-outline-variant/15 bg-surface-low/30 px-4 pb-4">
      {(execution.traceId || traceUrl) && (
        <div className="mt-3 flex items-center gap-3 text-xs font-label text-on-surface-variant">
          {execution.traceId && <span className="font-mono">trace_id: {execution.traceId}</span>}
          {traceUrl && (
            <a
              href={traceUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 px-2 py-1 rounded bg-primary/10 text-primary hover:bg-primary/20 transition-colors"
            >
              <Launch size={12} />
              {t('executions:openInTracing', { backend: traceBackendName ?? t('executions:tracingFallback') })}
            </a>
          )}
        </div>
      )}
      {execution.errorMessage && (
        <div className="mt-3 p-2.5 bg-error-container/30 text-error rounded-md text-xs font-label">
          {execution.errorMessage}
        </div>
      )}
      {steps && steps.length > 0 ? (
        <table className="w-full mt-3 text-sm font-label">
          <thead className="text-on-surface-variant text-xs">
            <tr>
              <th className="text-left py-1.5 font-semibold">{t('executions:tableHeaders.step')}</th>
              <th className="text-left py-1.5 font-semibold">{t('executions:tableHeaders.type')}</th>
              <th className="text-left py-1.5 font-semibold">{t('executions:tableHeaders.status')}</th>
              <th className="text-left py-1.5 font-semibold">{t('executions:tableHeaders.output')}</th>
            </tr>
          </thead>
          <tbody>
            {steps.map((step) => {
              const outputParameters = parseOutputParametersJson(step.outputParametersJson);
              const outputParametersJsonText = outputParameters ? JSON.stringify(outputParameters, null, 2) : null;
              const hasOutput = Boolean(step.errorOutput || step.output || step.traceOutput || outputParametersJsonText);

              return (
                <tr key={step.id} className="border-t border-outline/40">
                  <td className="py-2 align-top">
                    <span className="font-mono text-xs text-on-surface">{step.stepId}</span>
                    {step.stepName && step.stepName !== step.stepId && (
                      <span className="block text-[11px] text-outline">{step.stepName}</span>
                    )}
                  </td>
                  <td className="py-2 text-on-surface-variant align-top">{step.stepType}</td>
                  <td className="py-2 align-top">
                    <span className={`inline-flex items-center whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-semibold ${statusColors[step.status] ?? 'bg-surface-high text-on-surface-variant'}`}>
                      {step.status}
                    </span>
                  </td>
                  <td className="py-2 text-xs max-w-md space-y-1.5 align-top">
                    {step.errorOutput ? (
                      <span className="text-error">{step.errorOutput}</span>
                    ) : step.output ? (
                      <pre className="text-on-surface-variant whitespace-pre-wrap max-h-32 overflow-y-auto bg-surface-high rounded p-1.5 text-[11px] font-mono">{step.output}</pre>
                    ) : !hasOutput ? (
                      <span className="text-outline">{t('executions:noOutput')}</span>
                    ) : null}
                    {outputParametersJsonText && (
                      <div className="rounded-md border border-outline-variant/20 bg-surface-high/80 overflow-hidden">
                        <div className="flex items-center gap-1.5 px-2 py-1 border-b border-outline-variant/20 text-[10px] font-semibold uppercase tracking-wide text-on-surface-variant">
                          <DataBase size={11} />
                          {t('executions:outputParameters')}
                        </div>
                        <pre className="text-on-surface whitespace-pre-wrap max-h-32 overflow-y-auto p-2 text-[11px] font-mono leading-relaxed">
                          {outputParametersJsonText}
                        </pre>
                      </div>
                    )}
                    {step.traceOutput && (
                      <details className="text-[11px]">
                        <summary className="cursor-pointer text-on-surface-variant hover:text-on-surface select-none">
                          {t('executions:transcript', { count: step.traceOutput.split('\n').length })}
                        </summary>
                        <pre className="mt-1 text-on-surface-variant whitespace-pre-wrap max-h-48 overflow-y-auto bg-surface-high rounded p-1.5 font-mono">{step.traceOutput}</pre>
                      </details>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ) : (
        <p className="mt-3 text-xs text-on-surface-variant font-label">
          {steps?.length === 0 ? t('executions:noStepsExecuted') : t('executions:loadingSteps')}
        </p>
      )}
    </div>
  );
}
