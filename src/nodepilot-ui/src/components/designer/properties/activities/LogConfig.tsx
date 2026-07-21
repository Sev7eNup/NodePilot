import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

export function LogConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  return (
    <>
      <Field label={t('config.log.level')}>
        <select
          value={(config.level as string) || 'info'}
          onChange={(e) => onUpdate({ level: e.target.value })}
          className="input-field"
        >
          <option value="info">{t('config.log.levelInfo')}</option>
          <option value="warning">{t('config.log.levelWarning')}</option>
          <option value="error">{t('config.log.levelError')}</option>
        </select>
      </Field>
      <VariableInsertField
        label={t('config.log.message')}
        value={(config.message as string) || ''}
        onChange={(v) => onUpdate({ message: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={3}
        placeholder={'Processed {{prevStep.param.count}} items on {{prevStep.param.host}}'}
      />
    </>
  );
}
