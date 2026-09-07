import { useEffect } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

const COMPRESSION_LEVELS = ['Optimal', 'Fastest', 'NoCompression'] as const;

export function ZipOperationConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const operation = (config.operation as string) || 'compress';
  const force = config.force === true;

  // Persist the default operation when the panel opens, so the saved config matches the value
  // the dropdown already shows. Without it the run fails on the missing operation key.
  useEffect(() => {
    if (config.operation === undefined) {
      onUpdate({ operation: 'compress' });
    }
  }, [config.operation, onUpdate]);

  return (
    <>
      <Field label={t('config.zipOperation.operation')}>
        <select
          value={operation}
          onChange={(e) => onUpdate({ operation: e.target.value })}
          className="input-field"
        >
          <option value="compress">{t('config.zipOperation.operationCompress')}</option>
          <option value="extract">{t('config.zipOperation.operationExtract')}</option>
        </select>
      </Field>

      <VariableInsertField
        label={operation === 'compress' ? 'Source (Datei, Verzeichnis oder Glob)' : 'Source (ZIP-Archiv)'}
        value={(config.source as string) || ''}
        onChange={(v) => onUpdate({ source: v })}
        upstreamVars={upstreamVars}
        placeholder={operation === 'compress' ? 'C:\\\\logs\\\\*.log' : 'C:\\\\Downloads\\\\bundle.zip'}
      />

      <VariableInsertField
        label={operation === 'compress' ? 'Destination (ZIP-Datei)' : 'Destination (Zielverzeichnis)'}
        value={(config.destination as string) || ''}
        onChange={(v) => onUpdate({ destination: v })}
        upstreamVars={upstreamVars}
        placeholder={operation === 'compress' ? 'C:\\\\out\\\\bundle.zip' : 'C:\\\\Extract'}
      />

      {operation === 'compress' && (
        <Field label={t('config.zipOperation.compressionLevel')}>
          <select
            value={(config.compressionLevel as string) || 'Optimal'}
            onChange={(e) => onUpdate({ compressionLevel: e.target.value })}
            className="input-field"
          >
            {COMPRESSION_LEVELS.map((c) => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>
        </Field>
      )}

      <Field label={t('config.zipOperation.force')}>
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={force}
            onChange={(e) => onUpdate({ force: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1 text-sm text-on-surface">
            {t(force ? 'config.zipOperation.forceOnHint' : 'config.zipOperation.forceOffHint')}
          </div>
        </label>
      </Field>

      {operation === 'compress' && (
        <div className="text-[11px] text-on-surface-variant leading-snug">
          <Trans
            t={t}
            i18nKey="config.zipOperation.compressNote"
            components={{ code: <code className="bg-surface-high px-1 rounded" /> }}
          />
        </div>
      )}
    </>
  );
}
