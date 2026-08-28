import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, ErrorFilled, WarningAltFilled } from '@carbon/icons-react';
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
 * Live-Ops view for phones, replacing the timeline below `lg` rather than shrinking it. A Gantt
 * chart needs horizontal room a narrow screen cannot give, so the same facts appear as a vertical
 * list ordered by what an operator needs first. Inputs and derivation match OpsTimeline
 * (`buildTimelineBars`), so both views agree on which runs exist; only the presentation differs.
 */

/** Live runs listed before the remainder is summarised. Capped because the list re-renders once
 *  per clock tick, and an unbounded one on a busy estate scrolls without end. */
const RUNNING_CAP = 25;
/** Finished runs listed. Short on purpose: "what just happened", not a history page. */
const FINISHED_CAP = 10;
/** Failures listed. Has its own budget so a busy success list cannot squeeze them out. */
const FAILED_CAP = 10;

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
  /** Long-running threshold from the snapshot meta; the same value the timeline flags with. */
  overdueMs: number;
  selectedExecutionId: string | null;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations', 'executions']);

  const { stuck, live, failed, finished } = useMemo(() => {
    const stuckBars: TimelineBarInput[] = [];
    const liveBars: TimelineBarInput[] = [];
    const failedBars: TimelineBarInput[] = [];
    const finishedBars: TimelineBarInput[] = [];

    for (const bar of buildTimelineBars(running, recent, locallySettled, scopedWorkflowIds)) {
      if (bar.completedAtMs === null && isActiveBarStatus(bar.status)) {
        (isOverdue(bar, nowMs, overdueMs) ? stuckBars : liveBars).push(bar);
      } else if (npStatusFromExecution(bar.status) === 'failed') {
        // Failures get their own section rather than a colour inside "just finished": on a busy
        // estate the capped newest entries can be all successes, hiding failures behind a count
        // the list never reaches. Cancellations stay in the general list because they are a
        // deliberate action, not an incident.
        failedBars.push(bar);
      } else {
        finishedBars.push(bar);
      }
    }

    // Oldest first among live runs: only the top of a phone list is taken in at a glance, and
    // the longest-running run is the one most likely to need attention.
    stuckBars.sort((a, b) => a.startedAtMs - b.startedAtMs);
    liveBars.sort((a, b) => a.startedAtMs - b.startedAtMs);
    // Newest completion first, so reading downward walks back into the past.
    const byNewest = (a: TimelineBarInput, b: TimelineBarInput) => (b.completedAtMs ?? 0) - (a.completedAtMs ?? 0);
    failedBars.sort(byNewest);
    finishedBars.sort(byNewest);

    return { stuck: stuckBars, live: liveBars, failed: failedBars, finished: finishedBars };
  }, [running, recent, locallySettled, scopedWorkflowIds, nowMs, overdueMs]);

  const nameFor = (workflowId: string) => nodesById.get(workflowId)?.name ?? workflowId;
  // Root-folder workflows carry "/" as their path, which says nothing on a screen this narrow,
  // so only a real folder gets its own row.
  const folderFor = (workflowId: string) => {
    const path = nodesById.get(workflowId)?.folderPath ?? '';
    return path === '/' ? '' : path;
  };

  const runningTotal = stuck.length + live.length;
  const shownLive = live.slice(0, RUNNING_CAP);
  const hiddenLive = live.length - shownLive.length;
  const shownFailed = failed.slice(0, FAILED_CAP);
  const hiddenFailed = failed.length - shownFailed.length;
  const shownFinished = finished.slice(0, FINISHED_CAP);
  const hiddenFinished = finished.length - shownFinished.length;

  const card = (bar: TimelineBarInput) => (
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
  );

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
        {failed.length > 0 && (
          <span className={STATUS_TEXT_CLASS.failed}>{t('operations:mobile.countFailed', { count: failed.length })}</span>
        )}
      </div>

      {stuck.length > 0 && (
        <Section
          title={t('operations:stuck.title')}
          icon={<WarningAltFilled size={13} aria-hidden="true" />}
          tone="warning"
        >
          {stuck.map(card)}
        </Section>
      )}

      {/* Failures sit above the running list because the top of a phone screen carries the
          whole message, and a broken run outranks a healthy one. */}
      {failed.length > 0 && (
        <Section
          title={t('operations:mobile.failed')}
          icon={<ErrorFilled size={13} aria-hidden="true" />}
          tone="error"
        >
          {shownFailed.map(card)}
          {hiddenFailed > 0 && (
            <p className="px-1 pt-1 text-xs text-on-surface-variant">
              {t('operations:mobile.moreFailed', { count: hiddenFailed })}
            </p>
          )}
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
            {shownLive.map(card)}
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
          {shownFinished.map(card)}
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
  tone?: 'warning' | 'error';
  children: React.ReactNode;
}>) {
  return (
    <section
      className={`rounded-2xl border bg-surface p-3 ${
        tone === 'warning' ? 'border-warning/40' : tone === 'error' ? 'border-error/40' : 'border-outline-variant'
      }`}
      aria-label={title}
    >
      <h2
        className={`mb-2 flex items-center gap-1.5 px-1 text-xs font-medium uppercase tracking-wide ${
          tone ? STATUS_TEXT_CLASS[tone === 'error' ? 'failed' : 'warning'] : 'text-on-surface-variant'
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
 * One run. Tapping it opens the same drilldown as the timeline, so every desktop action
 * (cancel, retry, cancel-all, quarantine) stays reachable from a phone.
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
                ago they ended. The two questions differ, so the wording never overlaps. */}
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
          {/* Only live bars from the snapshot carry activity, and only the finished count is
              reliable mid-run: DeferRunningStateWrite leaves no trustworthy total. */}
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
