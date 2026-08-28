import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Edit, Misuse, Close, ArrowUpLeft, TreeView, Restart, StopFilledAlt, WarningAltFilled } from '@carbon/icons-react';
import { getExecution } from '../../api/operations';
import { npStatusFromExecution, rawStatusLabelKey, STATUS_BADGE_CLASS } from '../../lib/statusTokens';
import { formatDuration } from '../../lib/opsTimeline';
import { formatTime } from '../../lib/format';
import { CopyButton } from '../common/CopyButton';

// Slide-over drilldown for a single execution, opened from a timeline bar or ticker entry.
// Fetches the detail row for error message, triggeredBy and the parent link; live context
// (status, elapsed) comes from props so it stays in sync with the store between refetches.

const ACTIVE = new Set(['Running', 'Pending', 'Paused']);

// Terminal states the retry endpoint accepts, mirroring the guard in ExecutionsController.Retry.
// TimedOut is excluded because the endpoint rejects it with 400.
const RETRYABLE = new Set(['Succeeded', 'Failed', 'Cancelled']);

export function OpsExecutionDrilldown({ executionId, workflowName, folderPath, callees, status, startedAtMs, completedAtMs, nowMs, canRun, canEdit, workflowEnabled, runningCount, activity, pendingAction, onCancel, onRetry, onCancelAll, onQuarantine, onOpenEditor, onSelectExecution, onClose }: Readonly<{
  executionId: string;
  workflowName: string;
  folderPath: string;
  /** Static call topology: what this workflow's definition calls (resolved names or raw refs). */
  callees: string[];
  /** Live status from the store/snapshot (authoritative over the fetched detail row). */
  status: string;
  startedAtMs: number | null;
  completedAtMs: number | null;
  nowMs: number;
  /** Per-workflow folder-Run right (cancel / retry / cancel-all). */
  canRun: boolean;
  /** Per-workflow folder-Edit right (disable / quarantine); stricter than canRun. */
  canEdit: boolean;
  workflowEnabled: boolean;
  runningCount: number;
  /**
   * Observed step activity for a live run; null for settled runs or when not enriched.
   * Never rendered as a percentage, because no reliable step total exists mid-run.
   */
  activity: { stepsFinished: number | null; lastCompletedStepName: string | null; lastProgressAtMs: number | null } | null;
  /** Which action is in flight, so only that button shows a pending state. */
  pendingAction: 'cancel' | 'retry' | 'cancelAll' | 'quarantine' | null;
  onCancel: (executionId: string) => void;
  onRetry: (executionId: string) => void;
  onCancelAll: () => void;
  onQuarantine: () => void;
  onOpenEditor: () => void;
  onSelectExecution: (executionId: string) => void;
  onClose: () => void;
}>) {
  const { t } = useTranslation(['operations', 'executions']);

  const { data: detail, isError } = useQuery({
    queryKey: ['ops-execution', executionId],
    queryFn: () => getExecution(executionId),
    refetchOnWindowFocus: false,
    staleTime: 10_000,
  });

  const npStatus = npStatusFromExecution(status);
  const labelKey = rawStatusLabelKey(status);
  const active = ACTIVE.has(status);
  const startMs = startedAtMs ?? (detail ? Date.parse(detail.startedAt) : null);
  const endMs = completedAtMs ?? (detail?.completedAt ? Date.parse(detail.completedAt) : null);
  const durationMs = startMs !== null ? (endMs ?? nowMs) - startMs : null;

  return (
    <aside className="absolute right-0 top-0 z-10 flex h-full w-80 max-w-[85%] flex-col border-l border-outline-variant bg-surface-low/95 backdrop-blur" aria-label={t('operations:drilldown.title')}>
      <div className="flex items-start justify-between gap-2 border-b border-outline-variant p-4">
        <div className="min-w-0">
          <div className="truncate font-label font-medium text-on-surface" title={workflowName}>{workflowName}</div>
          <div className="truncate text-xs text-on-surface-variant">{folderPath}</div>
        </div>
        <button onClick={onClose} className="rounded p-1 text-on-surface-variant hover:bg-surface-highest" aria-label={t('operations:drilldown.close')}>
          <Close size={16} />
        </button>
      </div>

      <div className="flex-1 space-y-4 overflow-y-auto p-4">
        <dl className="space-y-2 text-sm">
          <div className="flex items-center justify-between gap-2">
            <dt className="text-on-surface-variant">{t('operations:drilldown.executionId')}</dt>
            <dd className="flex min-w-0 items-center justify-end gap-1.5">
              <span className="truncate font-mono text-xs text-on-surface" title={executionId}>{executionId}</span>
              <CopyButton text={executionId} size={11} className="shrink-0 rounded p-0.5 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface" />
            </dd>
          </div>
          <div className="flex items-center justify-between gap-2">
            <dt className="text-on-surface-variant">{t('operations:drilldown.status')}</dt>
            <dd>
              <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_BADGE_CLASS[npStatus]}`}>
                {labelKey ? t(`executions:status.${labelKey}`) : status}
              </span>
            </dd>
          </div>
          {startMs !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.started')}</dt>
              <dd className="tabular-nums text-on-surface">{formatTime(startMs)}</dd>
            </div>
          )}
          {endMs !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.completed')}</dt>
              <dd className="tabular-nums text-on-surface">{formatTime(endMs)}</dd>
            </div>
          )}
          {durationMs !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.duration')}</dt>
              <dd className="tabular-nums text-on-surface">{formatDuration(durationMs)}</dd>
            </div>
          )}
          {/* Live run: steps finished plus when the last one finished. No "n of m" and no
              percentage, because DeferRunningStateWrite leaves no reliable total mid-run. */}
          {active && activity?.stepsFinished !== null && activity !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.stepsFinished')}</dt>
              <dd className="tabular-nums text-on-surface">{activity.stepsFinished}</dd>
            </div>
          )}
          {active && activity?.lastProgressAtMs != null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.lastProgress')}</dt>
              <dd className="min-w-0 truncate text-right text-on-surface" title={activity.lastCompletedStepName ?? undefined}>
                {t('operations:drilldown.lastProgressValue', {
                  value: formatDuration(nowMs - activity.lastProgressAtMs),
                  step: activity.lastCompletedStepName ?? '—',
                })}
              </dd>
            </div>
          )}
          {/* Terminal run: the counts are complete and meaningful here. */}
          {!active && (detail?.stepsTotal ?? 0) > 0 && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.steps')}</dt>
              <dd className="tabular-nums text-on-surface">{detail!.stepsCompleted} / {detail!.stepsTotal}</dd>
            </div>
          )}
          {detail?.triggeredBy && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.triggeredBy')}</dt>
              <dd className="truncate text-on-surface" title={detail.triggeredBy}>{detail.triggeredBy}</dd>
            </div>
          )}
        </dl>

        {detail?.parentExecutionId && detail.parentWorkflowName && (
          <button
            type="button"
            onClick={() => onSelectExecution(detail.parentExecutionId!)}
            className="flex w-full items-center gap-2 rounded-lg border border-outline-variant bg-surface px-3 py-2 text-left text-sm text-on-surface hover:bg-surface-high"
          >
            <ArrowUpLeft size={14} className="shrink-0 text-on-surface-variant" aria-hidden="true" />
            <span className="text-on-surface-variant">{t('operations:drilldown.parent')}</span>
            <span className="min-w-0 truncate font-medium" title={detail.parentWorkflowName}>{detail.parentWorkflowName}</span>
          </button>
        )}

        {callees.length > 0 && (
          <div>
            <div className="mb-1.5 flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-on-surface-variant">
              <TreeView size={13} aria-hidden="true" />
              {t('operations:drilldown.calls')}
            </div>
            <ul className="flex flex-wrap gap-1.5">
              {callees.map((name) => (
                <li key={name} className="rounded-full border border-outline-variant bg-surface px-2 py-0.5 text-xs text-on-surface" title={name}>
                  {name}
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* Which step broke, which the error message alone rarely says. Ordered by
            (StartedAt, Id) server-side; parallel branches can contribute several entries. */}
        {(detail?.failedSteps?.length ?? 0) > 0 && (
          <div>
            <div className="mb-1.5 flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-error">
              <Misuse size={13} aria-hidden="true" />
              {t('operations:drilldown.failedSteps')}
            </div>
            <ul className="flex flex-wrap gap-1.5">
              {detail!.failedSteps!.map((s) => {
                const label = s.stepName ?? s.stepId;
                return (
                  <li key={s.stepId} className={`rounded-full px-2 py-0.5 text-xs ${STATUS_BADGE_CLASS.failed}`} title={label}>
                    {label}
                  </li>
                );
              })}
            </ul>
          </div>
        )}

        {detail?.errorMessage && (
          <div>
            <div className="mb-1 text-xs font-medium uppercase tracking-wide text-error">{t('operations:drilldown.error')}</div>
            <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-error/5 p-2 text-xs text-error">{detail.errorMessage}</pre>
          </div>
        )}

        {isError && <p className="text-sm text-error">{t('operations:drilldown.loadFailed')}</p>}
      </div>

      <div className="space-y-2 border-t border-outline-variant p-4">
        {canRun && active && (
          <button
            onClick={() => onCancel(executionId)}
            disabled={pendingAction !== null}
            className="flex w-full items-center justify-center gap-2 rounded-lg border border-error/40 px-3 py-2 text-sm font-label text-error hover:bg-error/10 disabled:opacity-50"
          >
            <Misuse size={15} />{t('operations:drilldown.cancel')}
          </button>
        )}

        {/* Retry stays visible but disabled on a quarantined workflow: the endpoint returns
            400 there, and a disabled button with a reason is clearer than a missing one. */}
        {canRun && RETRYABLE.has(status) && (
          <button
            onClick={() => onRetry(executionId)}
            disabled={pendingAction !== null || !workflowEnabled}
            title={workflowEnabled ? undefined : t('operations:drilldown.retryDisabledWorkflow')}
            className="flex w-full items-center justify-center gap-2 rounded-lg border border-outline-variant px-3 py-2 text-sm font-label text-on-surface hover:bg-surface-high disabled:opacity-50"
          >
            <Restart size={15} />{t('operations:drilldown.retry')}
          </button>
        )}

        {canRun && runningCount > 0 && (
          <button
            onClick={onCancelAll}
            disabled={pendingAction !== null}
            className="flex w-full items-center justify-center gap-2 rounded-lg border border-error/40 px-3 py-2 text-sm font-label text-error hover:bg-error/10 disabled:opacity-50"
          >
            <StopFilledAlt size={15} />{t('operations:drilldown.cancelAll', { count: runningCount })}
          </button>
        )}

        {/* Quarantine combines disable and cancel-all. Separated visually and gated on
            folder-Edit, which disable requires and cancel does not. */}
        {canEdit && (workflowEnabled || runningCount > 0) && (
          <button
            onClick={onQuarantine}
            disabled={pendingAction !== null}
            className="mt-3 flex w-full items-center justify-center gap-2 rounded-lg border-2 border-error bg-error/5 px-3 py-2 text-sm font-label font-medium text-error hover:bg-error/15 disabled:opacity-50"
          >
            <WarningAltFilled size={15} />{t('operations:drilldown.quarantine')}
          </button>
        )}

        <button
          onClick={onOpenEditor}
          className="flex w-full items-center justify-center gap-2 rounded-lg bg-surface-highest px-3 py-2 text-sm font-label text-on-surface hover:bg-surface-highest/80"
        >
          <Edit size={15} />{t('operations:drilldown.openEditor')}
        </button>
      </div>
    </aside>
  );
}
