import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';

export function ManualTriggerConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation(['triggers', 'common']);
  const parameters = (config.parameters as Array<Record<string, unknown>>) || [];

  const addParameter = () => {
    onUpdate({ parameters: [...parameters, { name: '', type: 'string', required: false, default: '' }] });
  };

  const updateParameter = (index: number, patch: Record<string, unknown>) => {
    const updated = parameters.map((p, i) => (i === index ? { ...p, ...patch } : p));
    onUpdate({ parameters: updated });
  };

  const removeParameter = (index: number) => {
    onUpdate({ parameters: parameters.filter((_, i) => i !== index) });
  };

  return (
    <>
      <Field label={t('triggers:manualTrigger.title')}>
        <input
          type="text"
          value={(config.title as string) || ''}
          onChange={(e) => onUpdate({ title: e.target.value })}
          className="input-field"
          placeholder={t('triggers:manualTrigger.titlePlaceholder')}
        />
      </Field>
      <Field label={t('common:description')}>
        <textarea
          value={(config.description as string) || ''}
          onChange={(e) => onUpdate({ description: e.target.value })}
          className="input-field"
          rows={2}
          placeholder={t('triggers:manualTrigger.descriptionPlaceholder')}
        />
      </Field>

      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <label className="font-label text-xs font-semibold text-on-surface-variant">{t('triggers:manualTrigger.inputParameters')}</label>
          <button onClick={addParameter} className="text-primary hover:text-primary-container text-xs font-label font-medium transition-colors">
            {t('common:add')}
          </button>
        </div>
        {parameters.map((param, i) => (
          <div key={i} className="bg-surface-high rounded-md p-3 space-y-2 relative">
            <button
              onClick={() => removeParameter(i)}
              className="absolute top-2 right-2 text-error hover:text-on-error-container text-sm font-bold leading-none"
              title={t('triggers:common.removeParameter')}
            >
              &times;
            </button>

            <div>
              <label className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">{t('common:name')}</label>
              <input
                type="text"
                value={(param.name as string) || ''}
                onChange={(e) => updateParameter(i, { name: e.target.value.replaceAll(/\s/g, '_') })}
                className="input-field font-mono text-sm"
                placeholder={t('triggers:manualTrigger.namePlaceholder')}
              />
            </div>

            <div className="flex gap-2">
              <div className="flex-1">
                <label className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">{t('common:type')}</label>
                <select
                  value={(param.type as string) || 'string'}
                  onChange={(e) => updateParameter(i, { type: e.target.value })}
                  className="input-field"
                >
                  <option value="string">{t('triggers:manualTrigger.typeString')}</option>
                  <option value="number">{t('triggers:manualTrigger.typeNumber')}</option>
                  <option value="boolean">{t('triggers:manualTrigger.typeBoolean')}</option>
                  <option value="select">{t('triggers:manualTrigger.typeSelect')}</option>
                </select>
              </div>
              <div className="flex-1">
                <label className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">{t('triggers:manualTrigger.default')}</label>
                <input
                  type="text"
                  value={(param.default as string) || ''}
                  onChange={(e) => updateParameter(i, { default: e.target.value })}
                  className="input-field"
                  placeholder={t('triggers:manualTrigger.defaultPlaceholder')}
                />
              </div>
            </div>

            <label className="flex items-center gap-2 text-xs font-label text-on-surface-variant">
              <input
                type="checkbox"
                checked={(param.required as boolean) || false}
                onChange={(e) => updateParameter(i, { required: e.target.checked })}
                className="rounded"
              />
              {t('triggers:common.required')}
            </label>

            {(param.name as string) && (
              <div className="text-[10px] font-mono text-outline bg-surface-container rounded px-2 py-1">
                {t('triggers:manualTrigger.accessVia')} <span className="text-primary">{'{{'}varName.param.{(param.name as string)}{'}}' }</span>
              </div>
            )}
          </div>
        ))}
        {parameters.length === 0 && (
          <p className="text-xs text-on-surface-variant font-label">{t('triggers:manualTrigger.noParameters')}</p>
        )}
      </div>
    </>
  );
}
