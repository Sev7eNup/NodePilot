import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { CodeField, FieldGrid, Section } from '../panelChrome';

/**
 * `config.headers` may be authored as an object (`{ "Content-Type": "application/json" }`) or as
 * the newline `Key: Value` string this field edits — the engine's `ParseHeaders` accepts both.
 * Render either shape as text so an object-valued node doesn't crash the panel; edits save the
 * string form (still accepted by the engine).
 */
function headersToText(headers: unknown): string {
  if (typeof headers === 'string') return headers;
  if (headers && typeof headers === 'object' && !Array.isArray(headers)) {
    return Object.entries(headers as Record<string, unknown>)
      .map(([key, value]) => `${key}: ${value}`)
      .join('\n');
  }
  return '';
}

export function RestApiConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const proxyMode = (config.proxyMode as string) || 'default';
  const method = (config.method as string) || 'GET';
  const hasBody = method === 'POST' || method === 'PUT' || method === 'PATCH';
  const proxyBadge =
    proxyMode === 'default' ? t('config.restApi.proxyBadgeDefault')
    : proxyMode === 'custom' ? t('config.restApi.proxyBadgeCustom')
    : t('config.restApi.proxyBadgeDirect');
  return (
    <>
      <FieldGrid>
        <Field label={t('config.restApi.method')}>
          <select
            value={method}
            onChange={(e) => onUpdate({ method: e.target.value })}
            className="input-field"
          >
            <option value="GET">GET</option>
            <option value="POST">POST</option>
            <option value="PUT">PUT</option>
            <option value="PATCH">PATCH</option>
            <option value="DELETE">DELETE</option>
            <option value="HEAD">HEAD</option>
          </select>
        </Field>
        <VariableInsertField
          label={t('config.restApi.url')}
          value={(config.url as string) || ''}
          onChange={(v) => onUpdate({ url: v })}
          upstreamVars={upstreamVars}
          placeholder="https://api.example.com/data"
        />
      </FieldGrid>

      <VariableInsertField
        label={t('config.restApi.headers')}
        value={headersToText(config.headers)}
        onChange={(v) => onUpdate({ headers: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={3}
        placeholder={'Content-Type: application/json\nAuthorization: Bearer …'}
        mono
      />

      {hasBody && (
        <Field label={t('config.restApi.body')}>
          <CodeField
            language="json"
            value={(config.body as string) || ''}
            onChange={(v) => onUpdate({ body: v })}
            upstreamVars={upstreamVars}
            minLines={6}
            placeholder='{"key": "{{prevStep.output}}"}'
          />
        </Field>
      )}

      <Section
        title={t('config.restApi.proxyMode')}
        collapsible
        nested
        defaultOpen={proxyMode !== 'default'}
        action={
          <span className="font-label text-[9px] text-outline">
            {proxyBadge}
          </span>
        }
      >
        <Field label={t('config.restApi.proxyModeLabel')}>
          <select
            value={proxyMode}
            onChange={(e) => onUpdate({ proxyMode: e.target.value })}
            className="input-field"
          >
            <option value="default">{t('config.restApi.proxyDefaultOption')}</option>
            <option value="custom">{t('config.restApi.proxyCustomOption')}</option>
            <option value="direct">{t('config.restApi.proxyDirectOption')}</option>
          </select>
        </Field>
        {proxyMode === 'custom' && (
          <>
            <VariableInsertField
              label={t('config.restApi.proxyAddress')}
              value={(config.proxyAddress as string) || ''}
              onChange={(v) => onUpdate({ proxyAddress: v })}
              upstreamVars={upstreamVars}
              placeholder="http://proxy.corp.local:8080"
            />
            <VariableInsertField
              label={t('config.restApi.noProxy')}
              value={(config.noProxy as string) || ''}
              onChange={(v) => onUpdate({ noProxy: v })}
              upstreamVars={upstreamVars}
              placeholder="*.internal, api.corp, 10.0.0.1"
            />
          </>
        )}
      </Section>
    </>
  );
}