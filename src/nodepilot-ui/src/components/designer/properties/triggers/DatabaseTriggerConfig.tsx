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
          <input
            type="number"
            value={(config.pollingIntervalSeconds as number) || 60}
            onChange={(e) => onUpdate({ pollingIntervalSeconds: parseInt(e.target.value) || 60 })}
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
        placeholder="SELECT * FROM Orders WHERE ProcessedAt IS NULL"
        mono
      />
    </>
  );
}
