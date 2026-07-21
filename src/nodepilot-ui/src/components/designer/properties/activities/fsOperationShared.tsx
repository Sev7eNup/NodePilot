import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import type { ConfigProps } from '../shared';
import { Field, VariableInsertField } from '../shared';
import { FieldGrid } from '../panelChrome';

export interface FsOperationOption {
  value: string;
  label: string;
}

export interface FsOperationConfigProps extends ConfigProps {
  operations: FsOperationOption[];
  pathLabel: string;
  pathPlaceholder: string;
  destinationPlaceholder: string;
  newNamePlaceholder: string;
}

/**
 * Shared implementation behind FileOperationConfig and FolderOperationConfig — both DSL panels
 * have the same operation/destination/new-name controls and only differ in the dropdown options,
 * placeholders, and labels.
 */
export function FsOperationConfig({
  config,
  onUpdate,
  upstreamVars = [],
  operations,
  pathLabel,
  pathPlaceholder,
  destinationPlaceholder,
  newNamePlaceholder,
}: Readonly<FsOperationConfigProps>) {
  const { t } = useTranslation('properties');
  const operation = (config.operation as string) || 'copy';

  useEffect(() => {
    if (!config.operation) {
      onUpdate({ operation: 'copy' });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const needsDestination = operation === 'copy' || operation === 'move';
  const needsNewName = operation === 'rename';

  return (
    <>
      <FieldGrid>
        <Field label={t('config.fsShared.operation')}>
          <select
            value={operation}
            onChange={(e) => onUpdate({ operation: e.target.value })}
            className="input-field"
          >
            {operations.map((op) => (
              <option key={op.value} value={op.value}>{op.label}</option>
            ))}
          </select>
        </Field>
        <VariableInsertField
          label={pathLabel}
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder={pathPlaceholder}
        />
      </FieldGrid>
      {needsDestination && (
        <VariableInsertField
          label={operation === 'copy' ? t('config.fsShared.copyTo') : t('config.fsShared.moveTo')}
          value={(config.destination as string) || ''}
          onChange={(v) => onUpdate({ destination: v })}
          upstreamVars={upstreamVars}
          placeholder={destinationPlaceholder}
        />
      )}
      {needsNewName && (
        <VariableInsertField
          label={t('config.fsShared.newName')}
          value={(config.newName as string) || ''}
          onChange={(v) => onUpdate({ newName: v })}
          upstreamVars={upstreamVars}
          placeholder={newNamePlaceholder}
        />
      )}
    </>
  );
}
