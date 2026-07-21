import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Edit, Misuse, Close, ArrowUpLeft, TreeView } from '@carbon/icons-react';
import { getExecution } from '../../api/operations';
import { npStatusFromExecution, rawStatusLabelKey, STATUS_BADGE_CLASS } from '../../lib/statusTokens';
import { formatDuration } from '../../lib/opsTimeline';

// Slide-over drilldown for a single execution (timeline bar / ticker click). Fetches the
// detail row for error message, triggeredBy and the parent link; live context (status,
// elapsed) comes from the props so it stays in sync with the store between refetches.

const ACTIVE = new Set(['Running', 'Pending', 'Paused']);

export function OpsExecutionDrilldown({ executionId, workflowName, folderPath, callees, status, startedAtMs, completedAtMs, nowMs, canCancel, cancelPending, onCancel, onOpenEditor, onSelectExecution, onClose }: Readonly<{
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
  canCancel: boolean;
  cancelPending: boolean;
  onCancel: (executionId: string) => void;
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
              <dd className="tabular-nums text-on-surface">{new Date(startMs).toLocaleTimeString()}</dd>
            </div>
          )}
          {endMs !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.completed')}</dt>
              <dd className="tabular-nums text-on-surface">{new Date(endMs).toLocaleTimeString()}</dd>
            </div>
          )}
          {durationMs !== null && (
            <div className="flex items-center justify-between gap-2">
              <dt className="text-on-surface-variant">{t('operations:drilldown.duration')}</dt>
              <dd className="tabular-nums text-on-surface">{formatDuration(durationMs)}</dd>
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

        {detail?.errorMessage && (
          <div>
            <div className="mb-1 text-xs font-medium uppercase tracking-wide text-error">{t('operations:drilldown.error')}</div>
            <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-error/5 p-2 text-xs text-error">{detail.errorMessage}</pre>
          </div>
        )}

        {isError && <p className="text-sm text-error">{t('operations:drilldown.loadFailed')}</p>}
      </div>

      <div className="space-y-2 border-t border-outline-variant p-4">
        {canCancel && active && (
          <button
            onClick={() => onCancel(executionId)}
            disabled={cancelPending}
            className="flex w-full items-center justify-center gap-2 rounded-lg border border-error/40 px-3 py-2 text-sm font-label text-error hover:bg-error/10 disabled:opacity-50"
          >
            <Misuse size={15} />{t('operations:drilldown.cancel')}
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
