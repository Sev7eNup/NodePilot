import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';

export function DelayConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  return (
    <Field label={t('config.delay.label')}>
      <input
        type="number"
        value={(config.seconds as number) || 5}
        onChange={(e) => onUpdate({ seconds: parseInt(e.target.value) || 5 })}
        className="input-field"
        min={1}
      />
    </Field>
  );
}
