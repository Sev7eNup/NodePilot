import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

const ALGORITHMS = ['SHA256', 'SHA1', 'MD5', 'SHA384', 'SHA512'] as const;

export function FileHashConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const algorithm = (config.algorithm as string) || 'SHA256';

  return (
    <>
      <VariableInsertField
        label={t('config.fileHash.path')}
        value={(config.path as string) || ''}
        onChange={(v) => onUpdate({ path: v })}
        upstreamVars={upstreamVars}
        placeholder="C:\\Setup\\package.msi"
      />
      <Field label={t('config.fileHash.algorithm')}>
        <select
          value={algorithm}
          onChange={(e) => onUpdate({ algorithm: e.target.value })}
          className="input-field"
        >
          {ALGORITHMS.map((a) => (
            <option key={a} value={a}>{a}</option>
          ))}
        </select>
      </Field>
      <VariableInsertField
        label={t('config.fileHash.expectedHash')}
        value={(config.expected as string) || ''}
        onChange={(v) => onUpdate({ expected: v })}
        upstreamVars={upstreamVars}
        placeholder="Hex-Wert — bei Mismatch failed der Step"
        mono
      />
      <div className="text-[11px] text-on-surface-variant leading-snug">
        Output: <code className="bg-surface-high px-1 rounded">{`{{step.param.hash}}`}</code>,
        {' '}<code className="bg-surface-high px-1 rounded">{`{{step.param.match}}`}</code>
        {' '}(<code className="bg-surface-high px-1 rounded">true</code>/<code className="bg-surface-high px-1 rounded">false</code>/leer wenn ohne Erwartung).
      </div>
    </>
  );
}
