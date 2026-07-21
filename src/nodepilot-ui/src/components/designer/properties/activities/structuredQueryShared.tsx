import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { CodeField, FieldGrid } from '../panelChrome';

export interface StructuredQueryConfigProps extends ConfigProps {
  language: 'xml' | 'json';
  contentLabel: string;
  filePlaceholder: string;
  queryField: string;
  queryLabel: string;
  queryPlaceholder: string;
  extraFields?: ReactNode;
}

/**
 * Shared frame for XmlQueryConfig and JsonQueryConfig — same Source/ResultMode dropdowns,
 * same inline/file content switch, same VariableInsertField query input. The activity-specific
 * differences (language tag, label text, query property name) are passed in as props.
 */
export function StructuredQueryConfig({
  config,
  onUpdate,
  upstreamVars = [],
  language,
  contentLabel,
  filePlaceholder,
  queryField,
  queryLabel,
  queryPlaceholder,
  extraFields,
}: Readonly<StructuredQueryConfigProps>) {
  const { t } = useTranslation('properties');
  const source = (config.source as string) || 'inline';
  return (
    <>
      <FieldGrid>
        <Field label={t('config.structuredQuery.source')}>
          <select value={source} onChange={(e) => onUpdate({ source: e.target.value })} className="input-field">
            <option value="inline">{t('config.structuredQuery.sourceInline')}</option>
            <option value="file">{t('config.structuredQuery.sourceFile')}</option>
          </select>
        </Field>
        <Field label={t('config.structuredQuery.resultMode')}>
          <select
            value={(config.resultMode as string) || 'single'}
            onChange={(e) => onUpdate({ resultMode: e.target.value })}
            className="input-field"
          >
            <option value="single">{t('config.structuredQuery.resultModeSingle')}</option>
            <option value="all">{t('config.structuredQuery.resultModeAll')}</option>
          </select>
        </Field>
      </FieldGrid>

      {source === 'file' ? (
        <VariableInsertField
          label={t('config.structuredQuery.filePath')}
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder={filePlaceholder}
          mono
        />
      ) : (
        <Field label={contentLabel}>
          <CodeField
            language={language}
            value={(config.content as string) || ''}
            onChange={(v) => onUpdate({ content: v })}
            upstreamVars={upstreamVars}
            minLines={6}
            placeholder={'{{prevStep.output}}'}
          />
        </Field>
      )}

      <VariableInsertField
        label={queryLabel}
        value={(config[queryField] as string) || ''}
        onChange={(v) => onUpdate({ [queryField]: v })}
        upstreamVars={upstreamVars}
        placeholder={queryPlaceholder}
        mono
      />

      {extraFields}
    </>
  );
}
