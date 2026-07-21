import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

export function EmailConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  return (
    <>
      <VariableInsertField
        label={t('config.email.to')}
        value={(config.to as string) || ''}
        onChange={(v) => onUpdate({ to: v })}
        upstreamVars={upstreamVars}
        placeholder="admin@company.com"
      />
      <VariableInsertField
        label={t('config.email.subject')}
        value={(config.subject as string) || ''}
        onChange={(v) => onUpdate({ subject: v })}
        upstreamVars={upstreamVars}
        placeholder={t('config.email.subjectPlaceholder')}
      />
      <VariableInsertField
        label={t('config.email.body')}
        value={(config.body as string) || ''}
        onChange={(v) => onUpdate({ body: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={5}
      />
      <Field label="">
        <label className="flex items-center gap-2 text-sm text-on-surface-variant">
          <input
            type="checkbox"
            checked={(config.isHtml as boolean) || false}
            onChange={(e) => onUpdate({ isHtml: e.target.checked })}
          />
          {t('config.email.htmlBody')}
        </label>
      </Field>
    </>
  );
}
