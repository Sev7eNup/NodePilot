import { Close } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';
import type { ManagedMachine } from '../../types/api';

interface Props {
  selectedNodes: Node[];
  machines: ManagedMachine[];
  onApply: (nodeIds: string[], patch: Record<string, unknown>, configPatch?: Record<string, unknown>) => void;
  onClose: () => void;
  width: number;
}

/**
 * Shown when ≥2 activity nodes are selected. Each field has its own Apply button so the
 * user can choose exactly which attribute to overwrite (vs an all-or-nothing form).
 *
 * `onApply` distinguishes:
 *   - top-level node.data fields (targetMachineId, disabled, outputVariable prefix)
 *   - nested config fields (timeoutSeconds, retry.*)
 */
export function BulkEditPanel({ selectedNodes, machines, onApply, onClose, width }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const [machineId, setMachineId] = useState<string>('');
  const [disabled, setDisabled] = useState<'true' | 'false' | ''>('');
  const [timeoutSec, setTimeoutSec] = useState<string>('');
  const [retryAttempts, setRetryAttempts] = useState<string>('');
  const [retryBackoff, setRetryBackoff] = useState<'fixed' | 'linear' | 'exponential' | ''>('');

  const ids = selectedNodes.map((n) => n.id);
  const activityCount = selectedNodes.filter((n) => n.type === 'activity').length;
  const nonActivityCount = selectedNodes.length - activityCount;

  const apply = (patch: Record<string, unknown>, configPatch?: Record<string, unknown>) => {
    onApply(ids, patch, configPatch);
  };

  return (
    <aside
      className="np-anim-panel bg-surface-low flex flex-col shrink-0 z-10 border-l border-outline-variant/15 overflow-y-auto"
      style={{ width }}
    >
      <div className="px-4 py-3 border-b border-outline-variant/15 flex items-center justify-between">
        <div>
          <h3 className="font-headline text-sm font-bold text-on-surface">{t('bulkEdit.title')}</h3>
          <p className="text-[11px] font-label text-on-surface-variant mt-0.5">
            {t('bulkEdit.selected', {
              count: activityCount,
              other: nonActivityCount > 0 ? t('bulkEdit.otherCount', { count: nonActivityCount }) : '',
            })}
          </p>
        </div>
        <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface transition-colors p-1">
          <Close size={16} />
        </button>
      </div>
      <div className="px-4 py-3 space-y-4 text-xs font-label">
        {/* Target machine */}
        <BulkField label={t('bulkEdit.targetMachine')}>
          <select
            value={machineId}
            onChange={(e) => setMachineId(e.target.value)}
            className="input-field text-xs"
          >
            <option value="">{t('bulkEdit.selectMachine')}</option>
            {machines.map((m) => (
              <option key={m.id} value={m.id}>{m.name} ({m.hostname})</option>
            ))}
          </select>
          <ApplyBtn disabled={!machineId} onClick={() => apply({ targetMachineId: machineId })}>
            {t('bulkEdit.applyMachine')}
          </ApplyBtn>
        </BulkField>

        {/* Disabled toggle */}
        <BulkField label={t('bulkEdit.enableDisable')}>
          <select
            value={disabled}
            onChange={(e) => setDisabled(e.target.value as 'true' | 'false' | '')}
            className="input-field text-xs"
          >
            <option value="">{t('bulkEdit.choose')}</option>
            <option value="true">{t('bulkEdit.disableAll')}</option>
            <option value="false">{t('bulkEdit.enableAll')}</option>
          </select>
          <ApplyBtn disabled={disabled === ''} onClick={() => apply({ disabled: disabled === 'true' })}>
            {t('bulkEdit.applyState')}
          </ApplyBtn>
        </BulkField>

        {/* Per-step timeout */}
        <BulkField label={t('bulkEdit.perStepTimeout')}>
          <input
            type="number"
            min={1}
            value={timeoutSec}
            onChange={(e) => setTimeoutSec(e.target.value)}
            placeholder={t('bulkEdit.timeoutPlaceholder')}
            className="input-field text-xs"
          />
          <ApplyBtn
            disabled={!timeoutSec || isNaN(Number(timeoutSec))}
            onClick={() => apply({}, { timeoutSeconds: Number(timeoutSec) })}
          >
            {t('bulkEdit.applyTimeout')}
          </ApplyBtn>
        </BulkField>

        {/* Retry policy */}
        <BulkField label={t('bulkEdit.retryPolicy')}>
          <div className="flex gap-2">
            <input
              type="number"
              min={1}
              max={10}
              value={retryAttempts}
              onChange={(e) => setRetryAttempts(e.target.value)}
              placeholder={t('bulkEdit.attemptsPlaceholder')}
              className="input-field text-xs flex-1"
            />
            <select
              value={retryBackoff}
              onChange={(e) => setRetryBackoff(e.target.value as 'fixed' | 'linear' | 'exponential' | '')}
              className="input-field text-xs flex-1"
            >
              <option value="">{t('bulkEdit.backoffPlaceholder')}</option>
              <option value="fixed">{t('bulkEdit.backoffFixed')}</option>
              <option value="linear">{t('bulkEdit.backoffLinear')}</option>
              <option value="exponential">{t('bulkEdit.backoffExponential')}</option>
            </select>
          </div>
          <ApplyBtn
            disabled={!retryAttempts || !retryBackoff}
            onClick={() => apply({}, {
              retry: {
                maxAttempts: Number(retryAttempts),
                backoff: retryBackoff,
                initialDelayMs: 1000,
                maxDelayMs: 30000,
              },
            })}
          >
            {t('bulkEdit.applyRetry')}
          </ApplyBtn>
        </BulkField>

        <div className="pt-2 border-t border-outline-variant/15">
          <p className="text-[10px] font-label text-on-surface-variant italic">
            {t('bulkEdit.note')}
          </p>
        </div>
      </div>
    </aside>
  );
}

function BulkField({ label, children }: Readonly<{ label: string; children: React.ReactNode }>) {
  return (
    <div className="space-y-1.5">
      <label className="block text-xs font-label font-semibold text-on-surface-variant">{label}</label>
      {children}
    </div>
  );
}

function ApplyBtn({ disabled, onClick, children }: Readonly<{ disabled?: boolean; onClick: () => void; children: React.ReactNode }>) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="text-[11px] font-label font-semibold text-primary hover:underline disabled:opacity-40 disabled:cursor-not-allowed disabled:no-underline"
    >
      {children}
    </button>
  );
}
