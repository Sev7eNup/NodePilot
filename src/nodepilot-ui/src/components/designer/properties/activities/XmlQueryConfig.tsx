import { useTranslation } from 'react-i18next';
import { VariableInsertField, type ConfigProps } from '../shared';
import { StructuredQueryConfig } from './structuredQueryShared';

export function XmlQueryConfig(props: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const { config, onUpdate, upstreamVars = [] } = props;
  const namespacesValue = typeof config.namespaces === 'object' && config.namespaces !== null
    ? JSON.stringify(config.namespaces)
    : ((config.namespaces as string) || '');
  return (
    <StructuredQueryConfig
      {...props}
      language="xml"
      contentLabel={t('config.structuredQuery.xmlContent')}
      filePlaceholder="C:\\Data\\input.xml"
      queryField="xpath"
      queryLabel={t('config.structuredQuery.xpathLabel')}
      queryPlaceholder="//book/title"
      extraFields={
        <VariableInsertField
          label={t('config.structuredQuery.namespaces')}
          value={namespacesValue}
          onChange={(v) => {
            try {
              onUpdate({ namespaces: v.trim() ? JSON.parse(v) : undefined });
            } catch {
              onUpdate({ namespaces: v });
            }
          }}
          upstreamVars={upstreamVars}
          multiline
          rows={2}
          placeholder={'{"bk": "http://example.com/books"}'}
          mono
        />
      }
    />
  );
}
