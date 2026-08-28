import { Close } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Workflow } from '../../types/api';

/**
 * Sets a workflow's concurrency limit — how many of its executions may run at once across
 * every caller. Its own dialog rather than a field in the editor, because the limit is an
 * operational setting: it needs no edit lock and does not create a new workflow version.
 */
export function ConcurrencyLimitDialog({
  workflow, onClose, onSave, isSaving,
}: Readonly<{
  workflow: Workflow;
  onClose: () => void;
  onSave: (limit: number | null) => void;
  isSaving: boolean;
}>) {
  const { t } = useTranslation(['workflows', 'common']);
  const [unlimited, setUnlimited] = useState(workflow.maxConcurrentExecutions == null);
  const [value, setValue] = useState(String(workflow.maxConcurrentExecutions ?? 5));

  const parsed = Number.parseInt(value, 10);
  const invalid = !unlimited && (Number.isNaN(parsed) || parsed < 1 || parsed > 1000);

  const submit = () => {
    if (invalid) return;
    onSave(unlimited ? null : parsed);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div className="w-full max-w-md rounded-xl bg-surface shadow-xl">
        <div className="flex items-center justify-between border-b border-outline-variant px-5 py-3">
          <h2 className="text-sm font-semibold text-on-surface">
            {t('workflows:concurrency.title')}
          </h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-1 text-on-surface-variant hover:bg-surface-high"
            aria-label={t('common:close')}
          >
            <Close size={16} />
          </button>
        </div>

        <div className="space-y-4 px-5 py-4">
          <p className="text-xs text-on-surface-variant">
            {t('workflows:concurrency.description', { name: workflow.name })}
          </p>

          <label className="flex items-center gap-2 text-sm text-on-surface">
            <input
              type="checkbox"
              checked={unlimited}
              onChange={(event) => setUnlimited(event.target.checked)}
            />
            {t('workflows:concurrency.unlimited')}
          </label>

          <div>
            <label
              htmlFor="np-concurrency-limit"
              className="mb-1 block text-xs font-medium text-on-surface-variant"
            >
              {t('workflows:concurrency.maxLabel')}
            </label>
            <input
              id="np-concurrency-limit"
              type="number"
              min={1}
              max={1000}
              value={value}
              disabled={unlimited}
              onChange={(event) => setValue(event.target.value)}
              className="w-32 rounded-lg border border-outline-variant bg-surface-low px-3 py-2 text-sm text-on-surface disabled:opacity-40"
            />
            {invalid && (
              <p className="mt-1 text-xs text-red-500">{t('workflows:concurrency.rangeError')}</p>
            )}
          </div>

          <p className="text-xs text-on-surface-variant">{t('workflows:concurrency.queueHint')}</p>
        </div>

        <div className="flex justify-end gap-2 border-t border-outline-variant px-5 py-3">
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg px-3 py-1.5 text-sm text-on-surface-variant hover:bg-surface-high"
          >
            {t('common:cancel')}
          </button>
          <button
            type="button"
            onClick={submit}
            disabled={invalid || isSaving}
            className="rounded-lg bg-primary px-3 py-1.5 text-sm text-on-primary disabled:opacity-40"
          >
            {t('common:save')}
          </button>
        </div>
      </div>
    </div>
  );
}
