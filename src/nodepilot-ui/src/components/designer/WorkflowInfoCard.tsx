import { FlashFilled } from '@carbon/icons-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { Workflow } from '../../types/api';
import { TRIGGER_META } from './workflowTriggerMeta';
import { formatDuration, formatRelative } from '../../lib/format';

interface Props {
  /** The workflow to describe — hovered row, or the currently-open workflow as fallback. */
  workflow: Workflow | null;
}

/**
 * Compact details panel shown beneath the (halved) workflow list in the browser. Pure
 * presentation: every field comes from the `Workflow` already loaded by the `['workflows']`
 * query, so there is no extra API call. Rows whose data is absent are omitted.
 */
export function WorkflowInfoCard({ workflow }: Readonly<Props>) {
  const { t } = useTranslation('designer');

  if (!workflow) {
    return (
      <div
        data-testid="workflow-info-card"
        className="px-4 py-4 text-[11px] font-label text-on-surface-variant text-center"
      >
        {t('infoCard.empty')}
      </div>
    );
  }

  const triggerType = workflow.triggerTypes?.[0];
  const meta = triggerType ? TRIGGER_META[triggerType] : undefined;
  const TriggerIcon = meta?.icon ?? FlashFilled;
  const triggerLabel = meta ? t(meta.labelKey) : t('browser.noTrigger');
  const last = workflow.lastExecution;

  return (
    <div data-testid="workflow-info-card" className="px-4 py-3 space-y-2">
      {/* Header: status dot + name + version */}
      <div className="flex items-center gap-2">
        <span
          className={`inline-block w-2 h-2 rounded-full shrink-0 ${workflow.isEnabled ? 'bg-emerald-500' : 'bg-outline'}`}
          aria-label={workflow.isEnabled ? t('browser.enabled') : t('browser.disabled')}
        />
        <span className="font-label text-xs font-semibold text-on-surface truncate flex-1" title={workflow.name}>
          {workflow.name}
        </span>
        <span className="text-[9px] font-label text-outline tabular-nums shrink-0">
          {t('infoCard.version', { n: workflow.version })}
        </span>
      </div>

      {workflow.description && (
        <p className="text-[11px] font-label text-on-surface-variant line-clamp-2">{workflow.description}</p>
      )}

      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[11px] font-label">
        <InfoRow label={t('infoCard.trigger')}>
          <span className="flex items-center gap-1 min-w-0">
            <TriggerIcon size={11} className={`shrink-0 ${meta?.color ?? 'text-on-surface-variant'}`} />
            <span className="truncate">{triggerLabel}</span>
          </span>
        </InfoRow>

        {workflow.activityCount != null && (
          <InfoRow label={t('infoCard.steps')}>{workflow.activityCount}</InfoRow>
        )}

        {last && (
          <InfoRow label={t('infoCard.lastRun')}>
            <span className="flex items-center gap-1.5 min-w-0">
              <span className="truncate">{t(`execution.status.${last.status}`, { defaultValue: last.status })}</span>
              <span className="text-outline shrink-0">·</span>
              <span className="shrink-0">{formatRelative(last.startedAt)}</span>
              {last.durationMs != null && (
                <>
                  <span className="text-outline shrink-0">·</span>
                  <span className="shrink-0">{formatDuration(last.durationMs)}</span>
                </>
              )}
            </span>
          </InfoRow>
        )}

        {workflow.successCount != null && workflow.totalCount != null && (
          <InfoRow label={t('infoCard.success')}>
            <span className="tabular-nums">{workflow.successCount}/{workflow.totalCount}</span>
          </InfoRow>
        )}

        {workflow.avgDurationMs != null && (
          <InfoRow label={t('infoCard.avgDuration')}>{formatDuration(workflow.avgDurationMs)}</InfoRow>
        )}

        {workflow.updatedAt && (
          <InfoRow label={t('infoCard.modified')}>
            {formatRelative(workflow.updatedAt)}{workflow.updatedBy ? ` · ${workflow.updatedBy}` : ''}
          </InfoRow>
        )}

        {workflow.checkedOutByUserName && (
          <InfoRow label={t('infoCard.lockedBy')}>
            {workflow.checkedOutByUserName}
            {workflow.checkedOutAt ? ` · ${formatRelative(workflow.checkedOutAt)}` : ''}
          </InfoRow>
        )}

        {workflow.folderPath && (
          <InfoRow label={t('infoCard.folder')}>
            <span className="truncate" title={workflow.folderPath}>{workflow.folderPath}</span>
          </InfoRow>
        )}
      </dl>
    </div>
  );
}

function InfoRow({ label, children }: Readonly<{ label: string; children: ReactNode }>) {
  return (
    <>
      <dt className="text-on-surface-variant whitespace-nowrap">{label}</dt>
      <dd className="text-on-surface min-w-0 truncate">{children}</dd>
    </>
  );
}
