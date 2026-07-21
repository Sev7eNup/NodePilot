import { Trans, useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';

export function JunctionConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const mode = (config.mode as string) || 'waitAll';
  return (
    <>
      <Field label={t('config.junction.mode')}>
        <select
          value={mode}
          onChange={(e) => onUpdate({ mode: e.target.value })}
          className="input-field"
        >
          <option value="waitAll">{t('config.junction.modeWaitAll')}</option>
          <option value="waitAny">{t('config.junction.modeWaitAny')}</option>
          <option value="waitNofM">{t('config.junction.modeWaitNofM')}</option>
        </select>
      </Field>

      {mode === 'waitNofM' && (
        <Field label={t('config.junction.requiredCount')}>
          <input
            type="number"
            value={(config.requiredCount as number) || 2}
            onChange={(e) => onUpdate({ requiredCount: parseInt(e.target.value) || 2 })}
            className="input-field"
            min={1}
          />
        </Field>
      )}

      {(mode === 'waitAny' || mode === 'waitNofM') && (
        <div className="bg-amber-50 border border-amber-200 rounded-md p-2.5 text-[11px] font-label text-amber-900">
          <Trans i18nKey="config.junction.skippedWarning" ns="properties" components={{ 1: <strong /> }} />
        </div>
      )}
    </>
  );
}
