import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

export function DatabaseTriggerConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('triggers');
  return (
    <>
      <FieldGrid>
        <Field label={t('databaseTrigger.connectionRef')}>
          <input
            type="text"
            value={(config.connectionRef as string) || ''}
            onChange={(e) => onUpdate({ connectionRef: e.target.value })}
            className="input-field"
            placeholder="Prod (Name unter Trigger:Database:Connections)"
          />
        </Field>
        <Field label={t('databaseTrigger.pollingInterval')}>
          {/* Falls back to the legacy `intervalSeconds` spelling so opening (and saving) an
              imported definition cannot drop a cadence the poll loop is honouring. 30 is the
              backend default — the field used to show 60 while the loop ran at 30. */}
          <input
            type="number"
            value={
              (config.pollingIntervalSeconds as number) ?? (config.intervalSeconds as number) ?? 30
            }
            onChange={(e) => onUpdate({ pollingIntervalSeconds: parseInt(e.target.value) || 30 })}
            className="input-field"
            min={5}
          />
        </Field>
      </FieldGrid>
      <VariableInsertField
        label={t('databaseTrigger.sqlQuery')}
        value={(config.query as string) || ''}
        onChange={(v) => onUpdate({ query: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={5}
        placeholder="SELECT MAX(Id) FROM Orders WHERE ProcessedAt IS NULL"
        mono
      />
      <p className="font-body text-xs text-on-surface-variant">{t('databaseTrigger.sentinelHint')}</p>
    </>
  );
}
