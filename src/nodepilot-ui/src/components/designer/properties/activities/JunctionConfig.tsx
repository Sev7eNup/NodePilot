import { Trans, useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';

/** Seeded when the author picks waitNofM. Written to the config, never only displayed. */
const SEEDED_REQUIRED_COUNT = 2;

export function JunctionConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const mode = (config.mode as string) || 'waitAll';
  // Falls back to 1, the value the engine uses when the key is absent — not to a display-only
  // default the engine never sees.
  const requiredCount = (config.requiredCount as number) || 1;
  return (
    <>
      <Field label={t('config.junction.mode')}>
        <select
          value={mode}
          onChange={(e) => {
            const next = e.target.value;
            // Seed requiredCount when waitNofM is chosen. It used to be a display-only default,
            // so a node saved without touching the number field ran as 1-of-M while the panel
            // showed 2 — and the non-waitAll fanout then cancelled the branches that had not
            // finished.
            onUpdate(
              next === 'waitNofM' && config.requiredCount === undefined
                ? { mode: next, requiredCount: SEEDED_REQUIRED_COUNT }
                : { mode: next },
            );
          }}
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
            value={requiredCount}
            onChange={(e) => onUpdate({ requiredCount: parseInt(e.target.value) || 1 })}
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
