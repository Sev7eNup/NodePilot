import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';

const MODES = ['alphanumeric', 'alphabetic', 'numeric', 'hex', 'guid', 'password', 'custom'] as const;

export function GenerateTextConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const mode = (config.mode as string) || 'alphanumeric';
  const excludeAmbiguous = config.excludeAmbiguous === true;

  return (
    <>
      <Field label={t('config.generateText.mode')}>
        <select
          value={mode}
          onChange={(e) => onUpdate({ mode: e.target.value })}
          className="input-field"
        >
          {MODES.map((m) => (
            <option key={m} value={m}>
              {t(`config.generateText.mode_${m}`)}
            </option>
          ))}
        </select>
      </Field>

      {mode !== 'guid' && (
        <Field label={t('config.generateText.length')}>
          <input
            type="number"
            value={(config.length as number) || 16}
            onChange={(e) => onUpdate({ length: parseInt(e.target.value) || 16 })}
            className="input-field"
            min={1}
            max={1024}
          />
        </Field>
      )}

      {mode === 'custom' && (
        <Field label={t('config.generateText.customCharset')}>
          <input
            type="text"
            value={(config.customCharset as string) || ''}
            onChange={(e) => onUpdate({ customCharset: e.target.value })}
            className="input-field font-mono"
            placeholder="ABCDEF0123456789"
          />
        </Field>
      )}

      {mode !== 'guid' && mode !== 'custom' && (
        <Field label={t('config.generateText.excludeAmbiguous')}>
          <label className="flex items-start gap-2 cursor-pointer select-none py-1">
            <input
              type="checkbox"
              checked={excludeAmbiguous}
              onChange={(e) => onUpdate({ excludeAmbiguous: e.target.checked })}
              className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
            />
            <div className="flex-1 text-sm text-on-surface">
              {t('config.generateText.excludeAmbiguousHint')}
            </div>
          </label>
        </Field>
      )}

      {mode === 'password' && (
        <div className="text-[11px] text-on-surface-variant leading-snug">
          {t('config.generateText.passwordHint')}
        </div>
      )}
    </>
  );
}
