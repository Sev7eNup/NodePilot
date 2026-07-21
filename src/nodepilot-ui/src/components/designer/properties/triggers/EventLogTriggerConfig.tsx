import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

export function EventLogTriggerConfig({ config, onUpdate }: Readonly<ConfigProps>) {
  const { t } = useTranslation('triggers');
  return (
    <>
      <FieldGrid>
        <Field label={t('eventLogTrigger.logName')}>
          <select
            value={(config.logName as string) || 'Application'}
            onChange={(e) => onUpdate({ logName: e.target.value })}
            className="input-field"
          >
            <option value="Application">Application</option>
            <option value="System">System</option>
            <option value="Security">Security</option>
            <option value="Setup">Setup</option>
          </select>
        </Field>
        <Field label={t('eventLogTrigger.entryType')}>
          <select
            value={(config.entryType as string) || ''}
            onChange={(e) => onUpdate({ entryType: e.target.value || undefined })}
            className="input-field"
          >
            <option value="">{t('eventLogTrigger.any')}</option>
            <option value="Error">Error</option>
            <option value="Warning">Warning</option>
            <option value="Information">Information</option>
            <option value="SuccessAudit">{t('eventLogTrigger.successAudit')}</option>
            <option value="FailureAudit">{t('eventLogTrigger.failureAudit')}</option>
          </select>
        </Field>
      </FieldGrid>
      <Field label={t('eventLogTrigger.sourceOptional')}>
        <input
          type="text"
          value={(config.source as string) || ''}
          onChange={(e) => onUpdate({ source: e.target.value })}
          className="input-field"
          placeholder={t('eventLogTrigger.sourcePlaceholder')}
        />
      </Field>
      <FieldGrid>
        <Field label={t('eventLogTrigger.eventIdOptional')}>
          <input
            type="number"
            value={(config.eventId as number) || ''}
            onChange={(e) => onUpdate({ eventId: e.target.value ? parseInt(e.target.value) : undefined })}
            className="input-field"
            placeholder={t('eventLogTrigger.eventIdPlaceholder')}
          />
        </Field>
        <Field label={t('eventLogTrigger.lookbackMinutes')}>
          <input
            type="number"
            value={(config.lookbackMinutes as number) || 5}
            onChange={(e) => onUpdate({ lookbackMinutes: parseInt(e.target.value) || 5 })}
            className="input-field"
            min={1}
          />
        </Field>
      </FieldGrid>
    </>
  );
}
