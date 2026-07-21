import { Time } from '@carbon/icons-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';
import { previewSchedule, relativeFromNow } from '../../../../lib/cronPreview';

export function ScheduleTriggerConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation(['triggers', 'common']);
  const presets = [
    { label: t('triggers:scheduleTrigger.presetEvery5Min'), cron: '0 */5 * * * ?' },
    { label: t('triggers:scheduleTrigger.presetEveryHour'), cron: '0 0 * * * ?' },
    { label: t('triggers:scheduleTrigger.presetDaily6Am'), cron: '0 0 6 * * ?' },
    { label: t('triggers:scheduleTrigger.presetMonFri8Am'), cron: '0 0 8 ? * MON-FRI' },
  ];

  // Preview of the next 5 fire times. Computed client-side (cron-parser) so the user gets
  // instant feedback without an API round-trip. On an invalid cron expression we show the
  // parser's error message prominently — helps catch typos early.
  const cron = (config.cronExpression as string) || '';
  const preview = useMemo(() => previewSchedule(cron, 5), [cron]);

  return (
    <>
      <Field label={t('triggers:scheduleTrigger.cronExpression')}>
        <input
          type="text"
          value={cron}
          onChange={(e) => onUpdate({ cronExpression: e.target.value })}
          className="input-field font-mono text-sm"
          placeholder="0 */5 * * * ?"
        />
      </Field>
      <div className="flex flex-wrap gap-1">
        {presets.map((p) => (
          <button
            key={p.cron}
            onClick={() => onUpdate({ cronExpression: p.cron })}
            className="px-2 py-1 text-[11px] font-label font-medium bg-surface-high text-on-surface-variant rounded hover:bg-surface-highest transition-colors"
          >
            {p.label}
          </button>
        ))}
      </div>
      {/* Next-fire-times preview: visually verifies the cron syntax. If the user types an
          invalid expression, they see the parser's error message immediately instead of only
          discovering a trigger-sync failure in the log file after saving. */}
      {cron.trim() && (
        <div className="rounded-md border border-outline-variant/30 bg-surface-high/50 p-2.5">
          <div className="flex items-center gap-1.5 mb-1.5">
            <Time size={12} className="text-on-surface-variant" />
            <span className="font-label text-[11px] font-semibold text-on-surface-variant uppercase tracking-wider">
              {t('triggers:scheduleTrigger.nextFireTimes')}
            </span>
          </div>
          {preview.error ? (
            <div className="text-[11px] font-mono text-error bg-error-container/20 rounded px-2 py-1">
              ⚠ {preview.error}
            </div>
          ) : preview.fireTimes.length === 0 ? (
            <div className="text-[11px] font-label text-on-surface-variant italic">{t('triggers:scheduleTrigger.noUpcomingFires')}</div>
          ) : (
            <ul className="space-y-0.5">
              {preview.fireTimes.map((d, i) => (
                <li key={i} className="flex items-center justify-between gap-2 text-[11px] font-mono tabular-nums">
                  <span className="text-on-surface">{d.toLocaleString(undefined, { hour12: false })}</span>
                  <span className="text-on-surface-variant text-[10px]">{relativeFromNow(d)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      <Field label={t('common:description')}>
        <input
          type="text"
          value={(config.description as string) || ''}
          onChange={(e) => onUpdate({ description: e.target.value })}
          className="input-field"
          placeholder={t('triggers:scheduleTrigger.descriptionPlaceholder')}
        />
      </Field>
    </>
  );
}
