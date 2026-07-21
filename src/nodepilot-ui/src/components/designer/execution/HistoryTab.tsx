import { ChevronDown, ChevronRight, ChevronUp, CircleDash, History, View } from '@carbon/icons-react';
import { useState, useMemo, useRef, memo } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Link } from 'react-router-dom';
import { api } from '../../../api/client';
import type { WorkflowExecution, StepExecution } from '../../../types/api';
import { ActivityTypeIcon, DurationBar, ExecutionStatusBadge, ExtrasCell, OutputBlock, StepInputBlock, StepOutputParametersBlock, StepStatusIcon, TriggerCell, firstLine, formatClock, formatMs, timeDiff } from './ExecutionPanelParts';
import { GanttChart, type GanttRow } from '../timeline/GanttChart';

/* ---- History Tab ---- */

type HistorySortKey = 'status' | 'workflow' | 'id' | 'trigger' | 'failedStep' | 'steps' | 'user' | 'started' | 'finished' | 'duration' | 'extras' | 'error';
type SortDirection = 'asc' | 'desc';

export function HistoryTab({ executions, scope, workflowNames, expandedId, onToggle, onReplay, activeReplayId, onScrubTime }: Readonly<{
  executions: WorkflowExecution[];
  scope: 'current' | 'all';
  workflowNames: Map<string, string>;
  expandedId: string | null;
  onToggle: (id: string) => void;
  onReplay?: (executionId: string) => void;
  activeReplayId?: string | null;
  onScrubTime?: (t: number | null) => void;
}>) {
  const { t } = useTranslation('designer');
  const [sort, setSort] = useState<{ key: HistorySortKey; direction: SortDirection }>({
    key: 'started',
    direction: 'desc',
  });

  const sortedExecutions = useMemo(() => {
    const direction = sort.direction === 'asc' ? 1 : -1;
    return [...executions].sort((a, b) => {
      const cmp = compareHistoryExecutions(a, b, sort.key, workflowNames);
      if (cmp !== 0) return cmp * direction;
      return new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime();
    });
  }, [executions, sort, workflowNames]);

  const setSortKey = (key: HistorySortKey) => {
    setSort((current) => current.key === key
      ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
      : { key, direction: defaultHistorySortDirection(key) });
  };

  // Virtualizing the history table: with several hundred terminal runs, the old
  // `sortedExecutions.map` approach triggered a full re-render of all rows on every resize
  // tick of the panel — visible as stutter while drag-resizing. @tanstack/react-virtual keeps
  // the DOM node count constant (~viewport + overscan), independent of list length. Sort +
  // expand stay client-side — `expandedId === exec.id` triggers the row growing, and
  // `measureElement` re-measures the new height afterward via ResizeObserver.
  const parentRef = useRef<HTMLDivElement>(null);
  const gridTemplate = useMemo(() => historyGridTemplate(scope), [scope]);

  const rowVirtualizer = useVirtualizer({
    count: sortedExecutions.length,
    getScrollElement: () => parentRef.current,
    // Initial estimate before the first measure: a collapsed row is ~26px (py-0.5 +
    // content), an expanded row ~320px (header + StepTimeline). measureElement corrects this
    // afterward.
    estimateSize: (index) => sortedExecutions[index].id === expandedId ? 320 : 26,
    overscan: 8,
    getItemKey: (index) => sortedExecutions[index].id,
  });

  if (executions.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-2 text-on-surface-variant">
        <History size={24} className="text-outline-variant" />
        <span className="font-label text-sm">{t('execution.history.noHistory')}</span>
      </div>
    );
  }

  return (
    <div ref={parentRef} className="overflow-auto h-full" role="table">
      {/* Sticky header above all rows. Uses the same grid-template-columns as the rows, so
          columns stay aligned even after switching from <table> to <div>+grid. */}
      <div
        role="row"
        className="sticky top-0 z-10 bg-surface-low text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wide grid items-center"
        style={{ gridTemplateColumns: gridTemplate }}
      >
        <SortableHistoryHeader label={t('execution.history.colStatus')} sortKey="status" sort={sort} onSort={setSortKey} className="px-3" />
        {scope === 'all' && <SortableHistoryHeader label={t('execution.history.colWorkflow')} sortKey="workflow" sort={sort} onSort={setSortKey} className="px-3" />}
        <SortableHistoryHeader label={t('execution.history.colId')} sortKey="id" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colTrigger')} sortKey="trigger" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colUser')} sortKey="user" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colSteps')} sortKey="steps" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colFailedStep')} sortKey="failedStep" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colStarted')} sortKey="started" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colFinished')} sortKey="finished" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colDuration')} sortKey="duration" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colExtras')} sortKey="extras" sort={sort} onSort={setSortKey} />
        <SortableHistoryHeader label={t('execution.history.colError')} sortKey="error" sort={sort} onSort={setSortKey} />
        <div className="px-3 py-1.5"></div>
      </div>

      <div style={{ height: rowVirtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {rowVirtualizer.getVirtualItems().map((vRow) => {
          const exec = sortedExecutions[vRow.index];
          return (
            <div
              key={exec.id}
              data-index={vRow.index}
              ref={rowVirtualizer.measureElement}
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${vRow.start}px)`,
              }}
            >
              <HistoryRow
                execution={exec}
                scope={scope}
                workflowName={workflowNames.get(exec.workflowId)}
                isExpanded={expandedId === exec.id}
                onToggle={() => onToggle(exec.id)}
                onReplay={onReplay}
                activeReplayId={activeReplayId}
                onScrubTime={onScrubTime}
                gridTemplate={gridTemplate}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
}

/** Grid template for the history table. The header and each row are independent grid
 *  containers (required by the @tanstack/react-virtual wrapper) — which means the column
 *  widths MUST be deterministic, otherwise CSS Grid's `auto` columns size differently per
 *  container depending on content width (header "Started" ~50px, row "7.5.2026, 11:49:29"
 *  ~140px → columns would drift out of alignment between header and rows). So: fixed px
 *  values for all content-dependent columns, `fr` only for the genuinely flexible columns
 *  (Trigger / User / Failed Step / Error / Workflow). The Workflow column only appears when
 *  `scope === 'all'`. Min-widths prevent squishing; if the panel gets too narrow, the
 *  container scrolls horizontally (overflow-auto). */
function historyGridTemplate(scope: 'current' | 'all'): string {
  const cols: string[] = [
    '130px',                  // Status (worst-case German label "Fehlgeschlagen" ~102px + px-3 cell padding)
    ...(scope === 'all' ? ['minmax(120px, 1.2fr)'] : []), // Workflow (flex)
    '80px',                   // ID (8-char mono + button padding)
    'minmax(120px, 1fr)',     // Trigger (flex)
    'minmax(80px, 0.6fr)',    // User (flex)
    '70px',                   // Steps ("X/Y" tabular)
    'minmax(80px, 1fr)',      // Failed Step (flex)
    '150px',                  // Started (locale date+time)
    '150px',                  // Finished (locale date+time)
    '70px',                   // Duration ("28ms" / "1.234s")
    '70px',                   // Extras (1-3 × 14 px Icons)
    'minmax(80px, 1.5fr)',    // Error (flex)
    '56px',                   // Actions
  ];
  return cols.join(' ');
}

function SortableHistoryHeader({ label, sortKey, sort, onSort, className = 'px-2' }: Readonly<{
  label: string;
  sortKey: HistorySortKey;
  sort: { key: HistorySortKey; direction: SortDirection };
  onSort: (key: HistorySortKey) => void;
  className?: string;
}>) {
  const { t } = useTranslation('designer');
  const active = sort.key === sortKey;
  return (
    <div
      role="columnheader"
      aria-sort={active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined}
      className={`text-left py-1.5 ${className}`}
    >
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        className={`inline-flex items-center gap-1 rounded px-1 py-0.5 -ml-1 transition-colors hover:bg-surface-high hover:text-on-surface ${
          active ? 'text-primary' : ''
        }`}
        title={t('execution.history.sortBy', { label })}
      >
        <span>{label}</span>
        {active
          ? sort.direction === 'asc'
            ? <ChevronUp size={11} />
            : <ChevronDown size={11} />
          : <ChevronUp size={11} className="opacity-0 group-hover:opacity-60" />}
      </button>
    </div>
  );
}

function defaultHistorySortDirection(key: HistorySortKey): SortDirection {
  // Numeric/time columns default their first click to desc (largest/newest first).
  return key === 'started' || key === 'finished' || key === 'duration' || key === 'extras' || key === 'steps'
    ? 'desc'
    : 'asc';
}

function compareHistoryExecutions(
  a: WorkflowExecution,
  b: WorkflowExecution,
  key: HistorySortKey,
  workflowNames: Map<string, string>,
): number {
  switch (key) {
    case 'status':
      return compareText(a.status, b.status);
    case 'workflow':
      return compareText(workflowNames.get(a.workflowId) ?? a.workflowId, workflowNames.get(b.workflowId) ?? b.workflowId);
    case 'id':
      return compareText(a.id, b.id);
    case 'trigger':
      return compareText(a.triggeredBy ?? '', b.triggeredBy ?? '');
    case 'user':
      return compareText(a.startedByUsername ?? '', b.startedByUsername ?? '');
    case 'steps':
      // Sort by the numerator of "completed/total" (i.e. how far the run got).
      // Runs without steps (e.g. cycle-only) stay at 0, sorted to the back.
      return compareNumber(a.stepsCompleted ?? 0, b.stepsCompleted ?? 0);
    case 'failedStep':
      // Sorted by the first failed step (name, falling back to step ID). With parallel
      // branches that have multiple failures, the first one determines the sort key — that's
      // enough, because the secondary tiebreak on StartedAt in the outer sortedExecutions call
      // takes care of the rest.
      return compareText(failedStepLabel(a), failedStepLabel(b));
    case 'started':
      return compareNumber(new Date(a.startedAt).getTime(), new Date(b.startedAt).getTime());
    case 'finished':
      return compareOptionalNumber(toTime(a.completedAt), toTime(b.completedAt));
    case 'duration':
      return compareOptionalNumber(executionDurationMs(a), executionDurationMs(b));
    case 'extras':
      return compareNumber(executionExtrasCount(a), executionExtrasCount(b));
    case 'error':
      return compareText(a.errorMessage ?? '', b.errorMessage ?? '');
  }
}

function compareText(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

function compareNumber(a: number, b: number): number {
  return a === b ? 0 : a < b ? -1 : 1;
}

function compareOptionalNumber(a: number | null, b: number | null): number {
  if (a == null && b == null) return 0;
  if (a == null) return 1;
  if (b == null) return -1;
  return compareNumber(a, b);
}

function toTime(value: string | null): number | null {
  return value ? new Date(value).getTime() : null;
}

function executionDurationMs(execution: WorkflowExecution): number | null {
  const completed = toTime(execution.completedAt);
  if (completed == null) return null;
  return completed - new Date(execution.startedAt).getTime();
}

/** Joined display string for the Failed-Step column. Falls back to step IDs when names
 *  are missing. Returns '' when there are no failures — caller renders "—" then. */
function failedStepLabel(execution: WorkflowExecution): string {
  const list = execution.failedSteps ?? [];
  if (list.length === 0) return '';
  return list.map((s) => s.stepName ?? s.stepId).join(', ');
}

function executionExtrasCount(execution: WorkflowExecution): number {
  return (execution.inputParametersJson ? 1 : 0)
    + (execution.returnData ? 1 : 0)
    + (execution.traceId ? 1 : 0);
}

const HistoryRow = memo(function HistoryRow({ execution, scope, workflowName, isExpanded, onToggle, onReplay, activeReplayId, onScrubTime, gridTemplate }: {
  execution: WorkflowExecution;
  scope: 'current' | 'all';
  workflowName?: string;
  isExpanded: boolean;
  onToggle: () => void;
  onReplay?: (executionId: string) => void;
  activeReplayId?: string | null;
  onScrubTime?: (t: number | null) => void;
  gridTemplate: string;
}) {
  const { t } = useTranslation('designer');
  const { data: steps } = useQuery({
    queryKey: ['execution-steps', execution.id],
    queryFn: () => api.get<StepExecution[]>(`/executions/${execution.id}/steps`),
    enabled: isExpanded,
  });

  return (
    <div className="border-b border-outline-variant/10">
      <div
        role="row"
        data-row-id={execution.id}
        onClick={onToggle}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onToggle()}
        tabIndex={0}
        className="cursor-pointer hover:bg-surface-low/50 transition-colors grid items-center"
        style={{ gridTemplateColumns: gridTemplate }}
      >
        <div className="px-3 py-0.5">
          <ExecutionStatusBadge status={execution.status} />
        </div>
        {scope === 'all' && (
          <div className="px-3 py-0.5 font-label text-[11px] font-medium text-on-surface min-w-0 truncate">
            <Link to={`/workflows/${execution.workflowId}`} className="hover:underline text-primary" onClick={e => e.stopPropagation()}>
              {workflowName ?? execution.workflowId.slice(0, 8) + '…'}
            </Link>
          </div>
        )}
        <div className="px-2 py-0.5">
          <button
            className="font-mono text-[10px] text-on-surface-variant hover:text-primary px-1.5 py-0.5 rounded hover:bg-surface-high transition-colors"
            onClick={(e) => { e.stopPropagation(); navigator.clipboard.writeText(execution.id).catch(() => {}); }}
            title={t('execution.history.copyFullId', { id: execution.id })}
          >
            {execution.id.slice(0, 8)}
          </button>
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant min-w-0">
          <div className="flex items-center gap-1.5 min-w-0">
            <TriggerCell triggeredBy={execution.triggeredBy} />
            {execution.parentExecutionId && (
              <span
                className="font-label text-[9px] font-semibold text-indigo-700 dark:text-indigo-400 bg-indigo-100 dark:bg-indigo-900/40 rounded px-1 py-0.5 whitespace-nowrap"
                title={execution.parentWorkflowName
                  ? t('execution.history.subWorkflowFrom', { name: execution.parentWorkflowName })
                  : t('execution.history.subWorkflowFromParent')}
              >
                ↳ {execution.parentWorkflowName ?? t('execution.history.subWfShort')}
              </span>
            )}
          </div>
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant whitespace-nowrap truncate">
          {execution.startedByUsername ?? <span className="text-outline">—</span>}
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant whitespace-nowrap font-mono tabular-nums">
          {(execution.stepsTotal ?? 0) > 0 ? (
            <span title={t('execution.history.stepsRanTooltip', { completed: execution.stepsCompleted ?? 0, total: execution.stepsTotal })}>
              {execution.stepsCompleted ?? 0}/{execution.stepsTotal}
            </span>
          ) : (
            <span className="text-outline">—</span>
          )}
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant min-w-0 truncate"
            title={failedStepLabel(execution)}>
          {(execution.failedSteps?.length ?? 0) > 0
            ? <span className="text-error">{failedStepLabel(execution)}</span>
            : <span className="text-outline">—</span>}
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant whitespace-nowrap" title={new Date(execution.startedAt).toISOString()}>
          {new Date(execution.startedAt).toLocaleString()}
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-on-surface-variant whitespace-nowrap" title={execution.completedAt ?? t('execution.history.notYetFinished')}>
          {execution.completedAt
            ? new Date(execution.completedAt).toLocaleString()
            : execution.status === 'Running'
              ? <span className="text-blue-500 flex items-center gap-1"><CircleDash size={10} className="animate-spin" /> {t('execution.history.running')}</span>
              : '—'}
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-outline whitespace-nowrap">
          {execution.completedAt ? timeDiff(execution.startedAt, execution.completedAt) : '—'}
        </div>
        <div className="px-2 py-0.5">
          <ExtrasCell execution={execution} />
        </div>
        <div className="px-2 py-0.5 font-label text-[11px] text-error min-w-0 truncate"
            title={execution.errorMessage ?? ''}>
          {execution.errorMessage ? firstLine(execution.errorMessage) : ''}
        </div>
        <div className="px-3 py-0.5">
          <div className="flex items-center gap-1.5">
            {onReplay && (
              <button
                onClick={(e) => { e.stopPropagation(); onReplay(execution.id); }}
                className={`p-0.5 rounded transition-colors ${
                  activeReplayId === execution.id
                    ? 'text-primary bg-primary/15'
                    : 'text-on-surface-variant hover:text-primary hover:bg-primary/10'
                }`}
                title={t('execution.history.showPathOnCanvas')}
              >
                <View size={13} />
              </button>
            )}
            <ChevronRight size={14} className={`text-outline transition-transform ${isExpanded ? 'rotate-90' : ''}`} />
          </div>
        </div>
      </div>
      {isExpanded && (
        <div className="bg-surface-low/30 px-4 py-3">
          {steps && steps.length > 0 ? (
            <StepTimeline
              steps={steps}
              executionStart={execution.startedAt}
              workflowId={execution.workflowId}
              onScrubTime={onScrubTime ? (t) => {
                // Auto-activate replay for THIS execution when user starts scrubbing,
                // so the canvas reflects the scrub time even if user didn't click "Show on canvas" first.
                if (t != null && onReplay && activeReplayId !== execution.id) onReplay(execution.id);
                onScrubTime(t);
              } : undefined}
            />
          ) : (
            <span className="font-label text-xs text-outline">
              {steps?.length === 0 ? t('execution.history.noStepsRecorded') : t('common:loadingDots')}
            </span>
          )}
        </div>
      )}
    </div>
  );
});

/**
 * A dense timeline view of an execution run. One row per step, showing status, name, relative
 * start (offset from execution start), duration, the gap to the previous step, and an inline
 * bar scaling the duration relative to the longest step. Clicking a row expands output/error
 * inline.
 *
 * Design decisions:
 *   - Relative offsets instead of absolute timestamps — people read "+2.3s after start" faster
 *     than "20:23:51.124". The absolute time is shown in the tooltip (hover).
 *   - The gap to the previous step is rendered separately, so "there was 1s of dead time
 *     between step 3 and 4" is immediately visible. Steps running in parallel get a ∥ icon
 *     instead of a negative gap.
 *   - Steps are sorted by <c>startedAt</c> — the API's own order mirrors persistence order,
 *     which isn't chronological for parallel branches.
 */
function StepTimeline({ steps, executionStart, workflowId, onScrubTime }: Readonly<{
  steps: StepExecution[]; executionStart: string; workflowId: string;
  onScrubTime?: (t: number | null) => void;
}>) {
  const { t } = useTranslation('designer');
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<'list' | 'gantt'>('list');
  const [scrubValue, setScrubValue] = useState<number | null>(null);

  // Sorted by StartedAt ascending; steps without a StartedAt (skipped) are appended
  // at the end. Skipped steps aren't rendered — they're control flow, not executed output.
  const allSorted = [...steps].sort((a, b) => {
    if (!a.startedAt) return 1;
    if (!b.startedAt) return -1;
    return new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime();
  });
  const skippedCount = allSorted.filter((s) => s.status === 'Skipped').length;
  const sorted = allSorted.filter((s) => s.status !== 'Skipped');

  const execStartMs = new Date(executionStart).getTime();
  const rows = sorted.map((s, i) => {
    const startMs = s.startedAt ? new Date(s.startedAt).getTime() : null;
    const endMs = s.completedAt ? new Date(s.completedAt).getTime() : null;
    const durationMs = startMs && endMs ? endMs - startMs : null;
    const offsetMs = startMs != null ? startMs - execStartMs : null;

    // Gap relative to the end of the PREVIOUS step in the sorted list. If the current
    // step started before the previous one ended → it's a parallel branch, marked visually
    // as such.
    const prev = i > 0 ? sorted[i - 1] : null;
    const prevEndMs = prev?.completedAt ? new Date(prev.completedAt).getTime() : null;
    const gapMs = startMs != null && prevEndMs != null ? startMs - prevEndMs : null;
    const isParallel = gapMs != null && gapMs < 0;

    return { step: s, startMs, endMs, durationMs, offsetMs, gapMs, isParallel };
  });

  const maxDuration = Math.max(1, ...rows.map((r) => r.durationMs ?? 0));
  const succeeded = allSorted.filter((s) => s.status === 'Succeeded').length;
  const failed = allSorted.filter((s) => s.status === 'Failed').length;
  const skipped = skippedCount; // shown in header but not rendered as rows
  const totalDuration = (() => {
    const firstStart = rows.find((r) => r.startMs != null)?.startMs;
    const lastEnd = rows.reduce((acc, r) => r.endMs && r.endMs > acc ? r.endMs : acc, 0);
    return firstStart && lastEnd ? lastEnd - firstStart : null;
  })();

  // Normalized rows for the shared GanttChart. Same source-of-truth as the list view —
  // both use `step.stepId` as the stable identifier so any future row-click → inspector
  // wiring stays in sync between the two views.
  const ganttRows: GanttRow[] = sorted.map((s) => ({
    id: s.stepId,
    name: s.stepName || s.stepId,
    status: s.status,
    startMs: s.startedAt ? new Date(s.startedAt).getTime() : null,
    endMs: s.completedAt ? new Date(s.completedAt).getTime() : null,
  }));

  return (
    <div className="space-y-1">
      {/* Header row: aggregates at a glance */}
      <div className="flex items-center gap-2 text-[11px] font-label text-on-surface-variant">
        <span className="font-semibold text-on-surface">{t('execution.timeline.stepsCount', { count: sorted.length })}</span>
        {totalDuration != null && <span>· {t('execution.timeline.total', { duration: formatMs(totalDuration) })}</span>}
        {succeeded > 0 && <span className="text-green-700">· {t('execution.timeline.ok', { count: succeeded })}</span>}
        {failed > 0 && <span className="text-error">· {t('execution.timeline.failed', { count: failed })}</span>}
        {skipped > 0 && <span className="text-outline">· {t('execution.timeline.skipped', { count: skipped })}</span>}
        <div className="ml-auto flex rounded-md overflow-hidden border border-outline-variant/20">
          <button
            type="button"
            onClick={() => setViewMode('list')}
            className={`px-2 h-5 text-[10px] font-label font-semibold transition-colors ${viewMode === 'list' ? 'bg-primary/15 text-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}
            title={t('execution.timeline.tableView')}
          >{t('execution.timeline.list')}</button>
          <button
            type="button"
            onClick={() => setViewMode('gantt')}
            className={`px-2 h-5 text-[10px] font-label font-semibold transition-colors border-l border-outline-variant/20 ${viewMode === 'gantt' ? 'bg-primary/15 text-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}
            title={t('execution.timeline.ganttView')}
          >{t('execution.timeline.gantt')}</button>
        </div>
      </div>

      {viewMode === 'gantt' && (
        <GanttChart
          rows={ganttRows}
          scrub={onScrubTime ? {
            value: scrubValue,
            onChange: (t) => {
              setScrubValue(t);
              onScrubTime(t);
            },
          } : undefined}
        />
      )}

      {viewMode === 'list' && (
      <>
      {/* Timeline rows. Grid template: #, Status, Name, Type, Start, End, Duration, Gap, Bar.
          Start + End are absolute HH:MM:SS.mmm — the user explicitly asked to see both times
          (the gap can be derived from them, but the absolute view is often more valuable for
          root-cause diagnosis ("what was running at 20:23:54?") than the relative offset). */}
      <div className="overflow-hidden rounded-md border border-outline-variant/20 bg-surface-lowest">
        <div className="grid grid-cols-[24px_18px_minmax(280px,1fr)_minmax(110px,auto)_88px_88px_72px_60px_minmax(90px,160px)] gap-x-2 px-2.5 py-1 bg-surface-low text-[10px] font-label font-bold text-outline uppercase tracking-wider">
          <span></span>
          <span></span>
          <span>{t('execution.timeline.colStep')}</span>
          <span>{t('execution.timeline.colType')}</span>
          <span className="text-right">{t('execution.timeline.colStart')}</span>
          <span className="text-right">{t('execution.timeline.colEnd')}</span>
          <span className="text-right">{t('execution.timeline.colDuration')}</span>
          <span className="text-right">{t('execution.timeline.colGap')}</span>
          <span>{t('execution.timeline.colBar')}</span>
        </div>
        {rows.map((r, i) => {
          const isOpen = expandedId === r.step.id;
          return (
            <div key={r.step.id}>
              <button
                type="button"
                onClick={() => setExpandedId(isOpen ? null : r.step.id)}
                className={`w-full grid grid-cols-[24px_18px_minmax(280px,1fr)_minmax(110px,auto)_88px_88px_72px_60px_minmax(90px,160px)] gap-x-2 items-center px-2.5 py-1 text-left text-xs border-t border-outline-variant/10 transition-colors hover:bg-surface-low cursor-pointer ${isOpen ? 'bg-surface-low' : ''}`}
              >
                <span className="font-label text-[11px] text-outline tabular-nums text-right">{i + 1}</span>
                <StepStatusIcon status={r.step.status} size={12} />
                <span className="font-label font-semibold text-on-surface truncate">
                  {r.step.stepName || r.step.stepId}
                </span>
                <span className="font-mono text-[11px] text-on-surface-variant truncate flex items-center gap-1.5">
                  <ActivityTypeIcon type={r.step.stepType} />
                  <span className="truncate">{r.step.stepType}</span>
                </span>
                <span className="font-mono text-[11px] text-on-surface-variant text-right tabular-nums"
                      title={r.startMs != null
                        ? t('execution.timeline.fromRunStart', { time: new Date(r.startMs).toISOString(), offset: formatMs(r.offsetMs ?? 0) })
                        : undefined}>
                  {r.startMs != null ? formatClock(r.startMs) : '—'}
                </span>
                <span className="font-mono text-[11px] text-on-surface-variant text-right tabular-nums"
                      title={r.endMs != null ? new Date(r.endMs).toISOString() : undefined}>
                  {r.endMs != null ? formatClock(r.endMs) : '—'}
                </span>
                <span className="font-mono text-[11px] text-on-surface text-right tabular-nums font-semibold">
                  {r.durationMs != null ? formatMs(r.durationMs) : '—'}
                </span>
                <span className={`font-mono text-[11px] text-right tabular-nums ${
                  r.isParallel ? 'text-primary'
                  : r.gapMs != null && r.gapMs >= 1000 ? 'text-amber-600'
                  : 'text-outline'
                }`} title={r.isParallel ? t('execution.timeline.parallelBranch') : undefined}>
                  {r.gapMs == null ? '—' : r.isParallel ? '∥' : `+${formatMs(r.gapMs)}`}
                </span>
                <DurationBar durationMs={r.durationMs} maxMs={maxDuration} status={r.step.status} />
              </button>
              {isOpen && (
                <div className="px-12 pb-3 pt-1.5 bg-surface-low/40 border-t border-outline-variant/10 space-y-2">
                  <StepInputBlock workflowId={workflowId} stepId={r.step.stepId} />
                  <StepOutputParametersBlock outputParametersJson={r.step.outputParametersJson} />
                  {r.step.output && (
                    <OutputBlock label={t('execution.timeline.output')} variant="default">{r.step.output}</OutputBlock>
                  )}
                  {r.step.errorOutput && (
                    <OutputBlock label={t('execution.timeline.error')} variant="error">{r.step.errorOutput}</OutputBlock>
                  )}
                  {r.step.traceOutput && (
                    <OutputBlock label={t('execution.timeline.transcript')} variant="default">{r.step.traceOutput}</OutputBlock>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
      </>
      )}
    </div>
  );
}

