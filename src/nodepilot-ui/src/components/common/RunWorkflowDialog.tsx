import { Close, Play } from '@carbon/icons-react';
import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';

interface ManualParameter {
  name: string;
  type: string;
  required: boolean;
  default: string;
}

interface Props {
  workflowName: string;
  triggerTitle: string;
  triggerDescription: string | null;
  parameters: ManualParameter[];
  /** Values from the most recent execution — used to prefill the form. Overrides defaults. */
  lastRunParams?: Record<string, string>;
  onExecute: (params: Record<string, string>) => void;
  onCancel: () => void;
}

export function RunWorkflowDialog({ workflowName, triggerTitle, triggerDescription, parameters, lastRunParams, onExecute, onCancel }: Readonly<Props>) {
  const { t } = useTranslation(['triggers', 'common']);
  const [values, setValues] = useState<Record<string, string>>({});

  useEffect(() => {
    // Initialize with defaults, then overlay last-run values where present.
    const init: Record<string, string> = {};
    for (const p of parameters) {
      if (p.default) init[p.name] = p.default;
      if (lastRunParams?.[p.name] !== undefined) init[p.name] = lastRunParams[p.name];
    }
    setValues(init);
  }, [parameters, lastRunParams]);

  const setValue = (name: string, value: string) => {
    setValues((prev) => ({ ...prev, [name]: value }));
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    onExecute(values);
  };

  const missingRequired = parameters
    .filter((p) => p.required && !values[p.name]?.trim())
    .map((p) => p.name);

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      aria-labelledby="run-workflow-dialog-title"
    >
      <div className="bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 w-full max-w-md mx-4">
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-surface-variant/50">
          <div>
            <h2 id="run-workflow-dialog-title" className="font-headline text-base font-bold text-on-surface">{triggerTitle || t('triggers:runDialog.title')}</h2>
            <p className="font-label text-xs text-on-surface-variant mt-0.5">{workflowName}</p>
          </div>
          <button
            onClick={onCancel}
            className="text-on-surface-variant hover:text-on-surface transition-colors p-1 rounded focus-visible:outline-2 focus-visible:outline-primary"
            aria-label={t('common:cancel')}
          >
            <Close size={18} aria-hidden="true" />
          </button>
        </div>

        <form onSubmit={handleSubmit}>
          <div className="px-6 py-4 space-y-4 max-h-[60vh] overflow-y-auto">
            {triggerDescription && (
              <p className="text-sm text-on-surface-variant font-label">{triggerDescription}</p>
            )}

            {parameters.length === 0 ? (
              <p className="text-sm text-on-surface-variant font-label py-2">
                {t('triggers:runDialog.noParameters')}
              </p>
            ) : (
              parameters.map((param) => {
                const lastVal = lastRunParams?.[param.name];
                const lastDiffersFromCurrent = lastVal !== undefined && lastVal !== values[param.name];
                return (
                <div key={param.name} className="space-y-1.5">
                  <label className="font-label text-xs font-semibold text-on-surface-variant flex items-center gap-1.5">
                    {param.name}
                    {param.required && <span className="text-error text-[10px]">{t('triggers:runDialog.fieldRequired')}</span>}
                    <span className="text-outline text-[10px]">({param.type})</span>
                    {lastVal !== undefined && (
                      <button
                        type="button"
                        onClick={() => setValue(param.name, lastVal)}
                        title={t('triggers:runDialog.lastRunUsed', { value: lastVal })}
                        className={`ml-auto text-[10px] font-label hover:underline ${lastDiffersFromCurrent ? 'text-primary' : 'text-outline'}`}
                      >
                        {t('triggers:runDialog.lastPrefix', { value: lastVal.length > 20 ? lastVal.slice(0, 20) + '…' : lastVal })}
                      </button>
                    )}
                  </label>

                  {param.type === 'boolean' ? (
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={values[param.name] === 'true'}
                        onChange={(e) => setValue(param.name, e.target.checked ? 'true' : 'false')}
                        className="rounded"
                      />
                      <span className="text-sm font-label text-on-surface">
                        {values[param.name] === 'true' ? t('common:yes') : t('common:no')}
                      </span>
                    </label>
                  ) : param.type === 'select' ? (
                    <input
                      type="text"
                      value={values[param.name] || ''}
                      onChange={(e) => setValue(param.name, e.target.value)}
                      className="input-field"
                      placeholder={param.default || t('triggers:runDialog.enterValue', { name: param.name })}
                    />
                  ) : param.type === 'number' ? (
                    <input
                      type="number"
                      value={values[param.name] || ''}
                      onChange={(e) => setValue(param.name, e.target.value)}
                      className="input-field"
                      placeholder={param.default || '0'}
                    />
                  ) : (
                    <input
                      type="text"
                      value={values[param.name] || ''}
                      onChange={(e) => setValue(param.name, e.target.value)}
                      className="input-field"
                      placeholder={param.default || t('triggers:runDialog.enterValue', { name: param.name })}
                    />
                  )}
                </div>
                );
              })
            )}
          </div>

          {/* Footer */}
          <div className="flex items-center justify-end gap-2 px-6 py-4 border-t border-surface-variant/50">
            <button
              type="button"
              onClick={onCancel}
              className="px-4 py-2 rounded-md text-sm font-label font-medium text-on-surface-variant hover:bg-surface-high transition-colors"
            >
              {t('common:cancel')}
            </button>
            <button
              type="submit"
              disabled={missingRequired.length > 0}
              className="flex items-center gap-1.5 px-5 py-2 rounded-md text-sm font-label font-medium bg-gradient-to-br from-primary to-primary-container text-on-primary shadow-sm hover:shadow-md transition-all disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Play size={14} />
              {t('triggers:runDialog.execute')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

/**
 * Extracts ManualTrigger config from a workflow definition JSON.
 * Returns null if no ManualTrigger node exists.
 */
export function extractManualTriggerConfig(definitionJson: string): {
  title: string;
  description: string | null;
  parameters: ManualParameter[];
} | null {
  try {
    const def = JSON.parse(definitionJson);
    const nodes = def.nodes || [];
    const triggerNode = nodes.find(
      (n: Record<string, unknown>) => {
        const data = n.data as Record<string, unknown> | undefined;
        return data?.activityType === 'manualTrigger';
      }
    );

    if (!triggerNode) return null;

    const data = triggerNode.data as Record<string, unknown>;
    const config = (data.config as Record<string, unknown>) || {};

    return {
      // Empty title makes RunWorkflowDialog fall back to the localized t('triggers:runDialog.title').
      title: (config.title as string) || '',
      description: (config.description as string) || null,
      parameters: ((config.parameters as ManualParameter[]) || []).map((p) => ({
        name: p.name || '',
        type: p.type || 'string',
        required: p.required || false,
        default: p.default || '',
      })),
    };
  } catch {
    return null;
  }
}
