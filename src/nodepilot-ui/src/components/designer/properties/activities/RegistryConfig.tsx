import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

const VALUE_TYPES = ['String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord'] as const;

// Which fields to show for each operation:
type OpSpec = {
  opKey: string;              // i18n key for the <option> label
  showValueName: boolean;     // show the value-name field
  requireValueName: boolean;  // value name is required (shown as a lint hint in the UI)
  showValueAndType: boolean;  // show the value-to-write + value-type fields
  hintKey: string;            // i18n key for the one-line hint below the operation dropdown
};

const OPS: Record<string, OpSpec> = {
  read:        { opKey: 'operationRead',       showValueName: true,  requireValueName: false, showValueAndType: false, hintKey: 'hintRead' },
  write:       { opKey: 'operationWrite',      showValueName: true,  requireValueName: true,  showValueAndType: true,  hintKey: 'hintWrite' },
  deleteValue: { opKey: 'operationDeleteValue', showValueName: true,  requireValueName: true,  showValueAndType: false, hintKey: 'hintDeleteValue' },
  deleteKey:   { opKey: 'operationDeleteKey',  showValueName: false, requireValueName: false, showValueAndType: false, hintKey: 'hintDeleteKey' },
  createKey:   { opKey: 'operationCreateKey',  showValueName: false, requireValueName: false, showValueAndType: false, hintKey: 'hintCreateKey' },
  exists:      { opKey: 'operationExists',     showValueName: true,  requireValueName: false, showValueAndType: false, hintKey: 'hintExists' },
  listSubKeys: { opKey: 'operationListSubKeys', showValueName: false, requireValueName: false, showValueAndType: false, hintKey: 'hintListSubKeys' },
  listValues:  { opKey: 'operationListValues', showValueName: false, requireValueName: false, showValueAndType: false, hintKey: 'hintListValues' },
};

export function RegistryConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const op = (config.operation as string) || 'read';
  const spec = OPS[op] ?? OPS.read;
  const valueType = (config.valueType as string) || 'String';

  return (
    <>
      <FieldGrid>
        <Field label={t('config.registry.operation')}>
          <select
            value={op}
            onChange={(e) => onUpdate({ operation: e.target.value })}
            className="input-field"
          >
            <option value="read">{t(`config.registry.${OPS.read.opKey}`)}</option>
            <option value="write">{t(`config.registry.${OPS.write.opKey}`)}</option>
            <option value="deleteValue">{t(`config.registry.${OPS.deleteValue.opKey}`)}</option>
            <option value="deleteKey">{t(`config.registry.${OPS.deleteKey.opKey}`)}</option>
            <option value="createKey">{t(`config.registry.${OPS.createKey.opKey}`)}</option>
            <option value="exists">{t(`config.registry.${OPS.exists.opKey}`)}</option>
            <option value="listSubKeys">{t(`config.registry.${OPS.listSubKeys.opKey}`)}</option>
            <option value="listValues">{t(`config.registry.${OPS.listValues.opKey}`)}</option>
          </select>
        </Field>
        {spec.showValueName && (
          <VariableInsertField
            label={spec.requireValueName ? t('config.registry.valueNameRequired') : t('config.registry.valueNameOptional')}
            value={(config.valueName as string) || ''}
            onChange={(v) => onUpdate({ valueName: v })}
            upstreamVars={upstreamVars}
            placeholder="Version"
          />
        )}
      </FieldGrid>
      <p className="text-xs text-gray-500 -mt-2">{t(`config.registry.${spec.hintKey}`)}</p>
      <VariableInsertField
        label={t('config.registry.keyPath')}
        value={(config.keyPath as string) || ''}
        onChange={(v) => onUpdate({ keyPath: v })}
        upstreamVars={upstreamVars}
        placeholder="HKLM:\SOFTWARE\MyApp"
      />
      {spec.showValueAndType && (
        <>
          <FieldGrid>
            <Field label={t('config.registry.valueType')}>
              <select
                value={valueType}
                onChange={(e) => onUpdate({ valueType: e.target.value })}
                className="input-field"
              >
                {VALUE_TYPES.map((vt) => (
                  <option key={vt} value={vt}>{vt}</option>
                ))}
              </select>
            </Field>
          </FieldGrid>
          <VariableInsertField
            label={t('config.registry.value')}
            value={(config.value as string) || ''}
            onChange={(v) => onUpdate({ value: v })}
            upstreamVars={upstreamVars}
            placeholder={
              valueType === 'DWord' || valueType === 'QWord' ? '42' :
              valueType === 'Binary' ? 'DEADBEEF (Hex, optional 0x-Prefix oder Trenner , ; : -)' :
              valueType === 'MultiString' ? 'Eintrag1\\nEintrag2\\nEintrag3 (eine Zeile pro Eintrag)' :
              ''
            }
          />
        </>
      )}
    </>
  );
}