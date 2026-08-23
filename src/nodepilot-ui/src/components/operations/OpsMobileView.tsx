import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, WarningAltFilled } from '@carbon/icons-react';
import type { OpsNode, OpsRecentExecution, OpsRunningExecution } from '../../types/api';
import type { LocalSettled } from '../../stores/operationsStore';
import {
  buildTimelineBars, formatDuration, isActiveBarStatus, isOverdue, type TimelineBarInput,
} from '../../lib/opsTimeline';
import {
  npStatusFromExecution, rawStatusLabelKey, STATUS_DOT_CLASS, STATUS_TEXT_CLASS,
} from '../../lib/statusTokens';
import { formatTime } from '../../lib/format';
import { EmptyState } from '../common/EmptyState';

/**
 * Live-Ops on a phone. Replaces the timeline below `lg` — it does NOT try to shrink it.
 *
 * A Gantt chart is a horizontal-time medium and a 390 px screen has no horizontal room to give:
 * measured, the lane-label column leaves ~190 px of track, so a 30-minute window renders at
 * ~6 px per minute and a typical run is a 4 px sliver with a truncated name beside it. No amount
 * of tuning changes that arithmetic, so the phone gets the same facts in the shape a phone is
 * good at — a vertical list, ordered by what an operator needs first.
 *
 * Same inputs as OpsTimeline and the same derivation (`buildTimelineBars`), so both views always
 * agree on which runs exist; only the presentation differs.
 */

/** Live runs listed before the remainder is summarised. Generous — but a phone re-renders this
 *  list once per clock tick, and an unbounded one on a busy estate is a scroll with no end. */
const RUNNING_CAP = 25;
/** Finished runs listed. Short on purpose: "what just happened", not a history page. */
const FINISHED_CAP = 10;

export function OpsMobileView({
  nowMs, running, recent, locallySettled, scopedWorkflowIds, nodesById, overdueMs,
  selectedExecutionId, onSelect,
}: Readonly<{
  nowMs: number;
  running: OpsRunningExecution[];
  recent: OpsRecentExecution[];
  locallySettled: Record<string, LocalSettled>;
  scopedWorkflowIds: Set<string>;
  nodesById: Map<string, OpsNode>;
  /** Long-running threshold from the snapshot meta — same value the timeline flags with. */
  overdueMs: number;
  selectedExecutionId: string | null;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations', 'executions']);

  const { stuck, live, finished, failedCount } = useMemo(() => {
    const stuckBars: TimelineBarInput[] = [];
    const liveBars: TimelineBarInput[] = [];
    const finishedBars: TimelineBarInput[] = [];
    let failed = 0;

    for (const bar of buildTimelineBars(running, recent, locallySettled, scopedWorkflowIds)) {
      if (bar.completedAtMs === null && isActiveBarStatus(bar.status)) {
        (isOverdue(bar, nowMs, overdueMs) ? stuckBars : liveBars).push(bar);
      } else {
        finishedBars.push(bar);
        if (npStatusFromExecution(bar.status) === 'failed') failed++;
      }
    }

    // Oldest first among live runs: the top of a phone list is all an operator takes in at a
    // glance, and the run that has been going longest is the one most likely to want them.
    stuckBars.sort((a, b) => a.startedAtMs - b.startedAtMs);
    liveBars.sort((a, b) => a.startedAtMs - b.startedAtMs);
    // Newest completion first — reading downward walks back into the past.
    finishedBars.sort((a, b) => (b.completedAtMs ?? 0) - (a.completedAtMs ?? 0));

    return { stuck: stuckBars, live: liveBars, finished: finishedBars, failedCount: failed };
  }, [running, recent, locallySettled, scopedWorkflowIds, nowMs, overdueMs]);

  const nameFor = (workflowId: string) => nodesById.get(workflowId)?.name ?? workflowId;
  // Root-folder workflows carry "/" as their path. Printing that under every second card is a
  // line of pure noise on a screen this narrow, so only a real folder earns the row.
  const folderFor = (workflowId: string) => {
    const path = nodesById.get(workflowId)?.folderPath ?? '';
    return path === '/' ? '' : path;
  };

  const runningTotal = stuck.length + live.length;
  const shownLive = live.slice(0, RUNNING_CAP);
  const hiddenLive = live.length - shownLive.length;
  const shownFinished = finished.slice(0, FINISHED_CAP);
  const hiddenFinished = finished.length - shownFinished.length;

  return (
    <div className="space-y-3" data-testid="ops-mobile">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-xs">
        <span className="flex items-center gap-1.5 text-on-surface">
          <span className={`h-2 w-2 rounded-full ${STATUS_DOT_CLASS.running}`} aria-hidden="true" />
          {t('operations:mobile.countRunning', { count: runningTotal })}
        </span>
        {stuck.length > 0 && (
          <span className={STATUS_TEXT_CLASS.warning}>{t('operations:mobile.countStuck', { count: stuck.length })}</span>
        )}
        {failedCount > 0 && (
          <span className={STATUS_TEXT_CLASS.failed}>{t('operations:mobile.countFailed', { count: failedCount })}</span>
        )}
      </div>

      {stuck.length > 0 && (
        <Section
          title={t('operations:stuck.title')}
          icon={<WarningAltFilled size={13} aria-hidden="true" />}
          tone="warning"
        >
          {stuck.map((bar) => (
            <RunCard
              key={bar.executionId}
              bar={bar}
              nowMs={nowMs}
              name={nameFor(bar.workflowId)}
              folderPath={folderFor(bar.workflowId)}
              selected={bar.executionId === selectedExecutionId}
              onSelect={onSelect}
              t={t}
            />
          ))}
        </Section>
      )}

      <Section title={t('operations:mobile.running')}>
        {shownLive.length === 0 && stuck.length === 0 ? (
          <EmptyState
            icon={<Activity size={20} />}
            title={t('operations:timeline.idle')}
            hint={t('operations:mobile.idleHint')}
            compact
          />
        ) : (
          <>
            {shownLive.map((bar) => (
              <RunCard
                key={bar.executionId}
                bar={bar}
                nowMs={nowMs}
                name={nameFor(bar.workflowId)}
                folderPath={folderFor(bar.workflowId)}
                selected={bar.executionId === selectedExecutionId}
                onSelect={onSelect}
                t={t}
              />
            ))}
            {hiddenLive > 0 && (
              <p className="px-1 pt-1 text-xs text-on-surface-variant">
                {t('operations:mobile.moreRunning', { count: hiddenLive })}
              </p>
            )}
          </>
        )}
      </Section>

      {shownFinished.length > 0 && (
        <Section title={t('operations:mobile.finished')}>
          {shownFinished.map((bar) => (
            <RunCard
              key={bar.executionId}
              bar={bar}
              nowMs={nowMs}
              name={nameFor(bar.workflowId)}
              folderPath={folderFor(bar.workflowId)}
              selected={bar.executionId === selectedExecutionId}
              onSelect={onSelect}
              t={t}
            />
          ))}
          {hiddenFinished > 0 && (
            <p className="px-1 pt-1 text-xs text-on-surface-variant">
              {t('operations:mobile.moreFinished', { count: hiddenFinished })}
            </p>
          )}
        </Section>
      )}
    </div>
  );
}

function Section({
  title, icon, tone, children,
}: Readonly<{
  title: string;
  icon?: React.ReactNode;
  tone?: 'warning';
  children: React.ReactNode;
}>) {
  return (
    <section
      className={`rounded-2xl border bg-surface p-3 ${tone === 'warning' ? 'border-warning/40' : 'border-outline-variant'}`}
      aria-label={title}
    >
      <h2
        className={`mb-2 flex items-center gap-1.5 px-1 text-xs font-medium uppercase tracking-wide ${
          tone === 'warning' ? STATUS_TEXT_CLASS.warning : 'text-on-surface-variant'
        }`}
      >
        {icon}
        {title}
      </h2>
      <div className="space-y-1.5">{children}</div>
    </section>
  );
}

/**
 * One run. Tapping it opens the same drilldown the timeline opens, so every action an operator
 * has on a desktop (cancel, retry, cancel-all, quarantine) is reachable from a phone unchanged.
 */
function RunCard({
  bar, nowMs, name, folderPath, selected, onSelect, t,
}: Readonly<{
  bar: TimelineBarInput;
  nowMs: number;
  name: string;
  folderPath: string;
  selected: boolean;
  onSelect: (executionId: string) => void;
  t: (k: string, opts?: Record<string, unknown>) => string;
}>) {
  const npStatus = npStatusFromExecution(bar.status);
  const labelKey = rawStatusLabelKey(bar.status);
  const live = bar.completedAtMs === null;
  const durationMs = (bar.completedAtMs ?? nowMs) - bar.startedAtMs;

  return (
    <button
      type="button"
      onClick={() => onSelect(bar.executionId)}
      aria-pressed={selected}
      className={`w-full rounded-xl border px-3 py-2 text-left transition-colors active:bg-surface-high ${
        selected ? 'border-primary bg-surface-high' : 'border-outline-variant/50 bg-surface-low'
      }`}
    >
      <div className="flex items-start gap-2">
        <span
          className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${STATUS_DOT_CLASS[npStatus]} ${live ? 'animate-pulse' : ''}`}
          aria-hidden="true"
        />
        <div className="min-w-0 flex-1">
          <div className="line-clamp-2 break-words text-sm text-on-surface">{name}</div>
          {folderPath && <div className="truncate text-[11px] text-on-surface-variant">{folderPath}</div>}
          <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs tabular-nums text-on-surface-variant">
            {/* Live runs read as elapsed time; settled ones as how long they took and how long
                ago they ended — the two questions are different and never share a phrasing. */}
            {live ? (
              <span>{t('operations:stuck.since', { value: formatDuration(durationMs) })}</span>
            ) : (
              <>
                <span className={STATUS_TEXT_CLASS[npStatus]}>
                  {labelKey ? t(`executions:status.${labelKey}`) : bar.status}
                </span>
                <span>{formatDuration(durationMs)}</span>
                <span>{t('operations:mobile.ago', { value: formatDuration(nowMs - bar.completedAtMs!) })}</span>
              </>
            )}
          </div>
          {/* Only live bars from the snapshot carry activity, and only "finished" is honest
              mid-run — there is no trustworthy total while DeferRunningStateWrite is on. */}
          {live && bar.lastProgressAtMs !== null && (
            <div className="mt-0.5 truncate text-xs text-on-surface-variant">
              {t('operations:stuck.lastProgress', {
                value: formatDuration(nowMs - bar.lastProgressAtMs),
                step: bar.lastCompletedStepName ?? '—',
              })}
            </div>
          )}
        </div>
        {live && (
          <span className="shrink-0 text-[11px] tabular-nums text-on-surface-variant">
            {formatTime(bar.startedAtMs, { hour: '2-digit', minute: '2-digit' })}
          </span>
        )}
      </div>
    </button>
  );
}
