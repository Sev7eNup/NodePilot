import { Add, TrashCan } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

interface FieldMapping {
  name?: string;
  path?: string;
}

export function WebhookTriggerConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('triggers');
  const signatureMode = (config.signatureMode as string) || 'header';
  const isNodePilotHmacV2 = signatureMode === 'nodepilot-hmac-v2';
  const isLegacyHmac = signatureMode === 'hmac';
  const isHmacConfig = isNodePilotHmacV2 || isLegacyHmac;
  const mappings: FieldMapping[] = Array.isArray(config.fieldMappings)
    ? (config.fieldMappings as FieldMapping[])
    : [];

  const updateMapping = (index: number, patch: FieldMapping) => {
    const next = mappings.map((m, i) => (i === index ? { ...m, ...patch } : m));
    onUpdate({ fieldMappings: next });
  };
  const removeMapping = (index: number) => {
    const next = mappings.filter((_, i) => i !== index);
    onUpdate({ fieldMappings: next.length > 0 ? next : undefined });
  };

  return (
    <>
      <FieldGrid>
        <Field label={t('webhookTrigger.httpMethod')}>
          <select
            value={(config.method as string) || 'POST'}
            onChange={(e) => onUpdate({ method: e.target.value })}
            className="input-field"
          >
            <option value="POST">POST</option>
            <option value="PUT">PUT</option>
            <option value="GET">GET</option>
          </select>
        </Field>
        <VariableInsertField
          label={t('webhookTrigger.webhookPath')}
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder="/api/webhooks/my-workflow"
        />
      </FieldGrid>
      <Field label={t(isHmacConfig
        ? 'webhookTrigger.secretHmac'
        : 'webhookTrigger.secretOptional')}>
        <input
          type="password"
          value={(config.secret as string) || ''}
          onChange={(e) => onUpdate({ secret: e.target.value })}
          className="input-field"
          placeholder={t('webhookTrigger.secretPlaceholder')}
          autoComplete="off"
        />
      </Field>
      <Field label={t('webhookTrigger.signatureMode')}>
        <select
          value={signatureMode}
          onChange={(e) => {
            const mode = e.target.value;
            // header mode has no use for the hmac sub-keys — drop them so the saved
            // config doesn't carry dead fields.
            onUpdate(mode === 'nodepilot-hmac-v2'
              ? { signatureMode: 'nodepilot-hmac-v2' }
              : { signatureMode: undefined, signatureHeader: undefined, signaturePrefix: undefined });
          }}
          className="input-field"
        >
          <option value="header">{t('webhookTrigger.signatureModeHeader')}</option>
          {isLegacyHmac && (
            <option value="hmac" disabled>{t('webhookTrigger.signatureModeLegacy')}</option>
          )}
          <option value="nodepilot-hmac-v2">{t('webhookTrigger.signatureModeHmac')}</option>
        </select>
      </Field>
      {isHmacConfig && (
        <>
          <FieldGrid>
            <Field label={t('webhookTrigger.signatureHeader')}>
              <input
                type="text"
                value={(config.signatureHeader as string) || ''}
                onChange={(e) => onUpdate({ signatureHeader: e.target.value || undefined })}
                className="input-field"
                placeholder="X-NodePilot-Signature"
              />
            </Field>
            <Field label={t('webhookTrigger.signaturePrefix')}>
              <input
                type="text"
                value={(config.signaturePrefix as string) ?? 'sha256='}
                onChange={(e) => onUpdate({ signaturePrefix: e.target.value })}
                className="input-field"
                placeholder="sha256="
              />
            </Field>
          </FieldGrid>
          <p className={isLegacyHmac ? 'text-xs text-error' : 'text-xs text-on-surface-variant'}>
            {t(isLegacyHmac ? 'webhookTrigger.legacyHmacHint' : 'webhookTrigger.hmacHint')}
          </p>
        </>
      )}
      <Field label={t('webhookTrigger.fieldMappings')}>
        <div className="flex flex-col gap-1.5">
          {mappings.map((m, i) => (
            // Positional keys are correct here: rows are edited in place by index.
            (<div key={i} className="flex items-center gap-1.5">
              <input
                type="text"
                value={m.name || ''}
                onChange={(e) => updateMapping(i, { name: e.target.value })}
                className="input-field flex-1"
                placeholder={t('webhookTrigger.fieldName')}
                aria-label={t('webhookTrigger.fieldName')}
              />
              <input
                type="text"
                value={m.path || ''}
                onChange={(e) => updateMapping(i, { path: e.target.value })}
                className="input-field flex-[2] font-mono"
                placeholder="$.ticket.id"
                aria-label={t('webhookTrigger.fieldPath')}
              />
              <button
                type="button"
                onClick={() => removeMapping(i)}
                className="shrink-0 rounded p-1 text-on-surface-variant hover:text-error"
                title={t('webhookTrigger.removeMapping')}
                aria-label={t('webhookTrigger.removeMapping')}
              >
                <TrashCan size={14} />
              </button>
            </div>)
          ))}
          <button
            type="button"
            onClick={() => onUpdate({ fieldMappings: [...mappings, { name: '', path: '' }] })}
            className="flex w-fit items-center gap-1 rounded px-1.5 py-1 text-xs text-on-surface-variant hover:bg-surface-high hover:text-on-surface"
          >
            <Add size={13} /> {t('webhookTrigger.addMapping')}
          </button>
          <p className="text-xs text-on-surface-variant">{t('webhookTrigger.fieldMappingsHint')}</p>
        </div>
      </Field>
    </>
  );
}
