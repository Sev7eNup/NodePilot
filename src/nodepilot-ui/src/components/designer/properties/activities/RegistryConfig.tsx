import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

const VALUE_TYPES = ['String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord'] as const;

// Which fields to show for each operation:
type OpSpec = {
  showValueName: boolean;     // show the value-name field
  requireValueName: boolean;  // value name is required (shown as a lint hint in the UI)
  showValueAndType: boolean;  // show the value-to-write + value-type fields
  hint: string;               // one-line hint shown below the operation dropdown
};

const OPS: Record<string, OpSpec> = {
  read:        { showValueName: true,  requireValueName: false, showValueAndType: false, hint: 'Liest einen Value (mit Name) oder alle Values des Keys.' },
  write:       { showValueName: true,  requireValueName: true,  showValueAndType: true,  hint: 'Schreibt einen Value. Fehlende Keys werden automatisch angelegt.' },
  deleteValue: { showValueName: true,  requireValueName: true,  showValueAndType: false, hint: 'Entfernt einen einzelnen Value aus dem Key.' },
  deleteKey:   { showValueName: false, requireValueName: false, showValueAndType: false, hint: 'Entfernt den Key inkl. aller Sub-Keys (rekursiv).' },
  createKey:   { showValueName: false, requireValueName: false, showValueAndType: false, hint: 'Legt den Key idempotent an (no-op falls vorhanden).' },
  exists:      { showValueName: true,  requireValueName: false, showValueAndType: false, hint: 'Prüft Key-Existenz oder (mit Value-Name) Property-Existenz.' },
  listSubKeys: { showValueName: false, requireValueName: false, showValueAndType: false, hint: 'Listet die Sub-Key-Namen unter dem Pfad als Array.' },
  listValues:  { showValueName: false, requireValueName: false, showValueAndType: false, hint: 'Listet alle Values mit Name, Typ und Wert als Array.' },
};

export function RegistryConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const op = (config.operation as string) || 'read';
  const spec = OPS[op] ?? OPS.read;
  const valueType = (config.valueType as string) || 'String';

  return (
    <>
      <FieldGrid>
        <Field label="Operation">
          <select
            value={op}
            onChange={(e) => onUpdate({ operation: e.target.value })}
            className="input-field"
          >
            <option value="read">Read</option>
            <option value="write">Set Value (Create/Update)</option>
            <option value="deleteValue">Delete Value</option>
            <option value="deleteKey">Delete Key</option>
            <option value="createKey">Create Key</option>
            <option value="exists">Exists</option>
            <option value="listSubKeys">List Sub-Keys</option>
            <option value="listValues">List Values</option>
          </select>
        </Field>
        {spec.showValueName && (
          <VariableInsertField
            label={spec.requireValueName ? 'Value-Name *' : 'Value-Name (optional)'}
            value={(config.valueName as string) || ''}
            onChange={(v) => onUpdate({ valueName: v })}
            upstreamVars={upstreamVars}
            placeholder="Version"
          />
        )}
      </FieldGrid>
      <p className="text-xs text-gray-500 -mt-2">{spec.hint}</p>
      <VariableInsertField
        label="Key Path *"
        value={(config.keyPath as string) || ''}
        onChange={(v) => onUpdate({ keyPath: v })}
        upstreamVars={upstreamVars}
        placeholder="HKLM:\SOFTWARE\MyApp"
      />
      {spec.showValueAndType && (
        <>
          <FieldGrid>
            <Field label="Value-Typ">
              <select
                value={valueType}
                onChange={(e) => onUpdate({ valueType: e.target.value })}
                className="input-field"
              >
                {VALUE_TYPES.map((t) => (
                  <option key={t} value={t}>{t}</option>
                ))}
              </select>
            </Field>
          </FieldGrid>
          <VariableInsertField
            label="Wert"
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
