import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { getCustomActivityFacts } from '../../../../lib/customActivities';

/**
 * Generic config form for a user-authored custom activity. Renders one field per declared input
 * parameter (driven by the runtime catalog), writing each value into `config[name]`. String / number
 * / multiline fields use {@link VariableInsertField} so authors can wire `{{upstream}}` / `{{globals.X}}`
 * just like runScript. The definition reference (`config.__customKey`) is set at node-creation time.
 */
export function DynamicActivityConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const key = config.__customKey as string | undefined;
  const facts = key ? getCustomActivityFacts(`custom:${key}`) : undefined;

  if (!facts) {
    return (
      <p className="text-xs text-on-surface-variant">
        {t('customActivity.unavailable', 'This custom activity definition is unavailable (deleted or disabled).')}
      </p>
    );
  }
  if (facts.inputs.length === 0) {
    return (
      <p className="text-xs text-on-surface-variant">
        {t('customActivity.noInputs', 'This custom activity has no inputs.')}
      </p>
    );
  }

  return (
    <>
      {facts.inputs.map((p) => {
        const value = (config[p.name] as string | undefined) ?? '';
        const label = p.required ? `${p.label} *` : p.label;

        if (p.type === 'boolean') {
          return (
            <Field key={p.name} label={label}>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={value === 'true'}
                  onChange={(e) => onUpdate({ [p.name]: e.target.checked ? 'true' : 'false' })}
                />
                <span className="text-xs text-on-surface-variant">{value === 'true' ? 'true' : 'false'}</span>
              </label>
            </Field>
          );
        }

        if (p.type === 'select') {
          return (
            <Field key={p.name} label={label}>
              <select
                className="input-field"
                value={value}
                onChange={(e) => onUpdate({ [p.name]: e.target.value })}
              >
                <option value="">—</option>
                {(p.options ?? []).map((o) => (
                  <option key={o} value={o}>{o}</option>
                ))}
              </select>
            </Field>
          );
        }

        return (
          <VariableInsertField
            key={p.name}
            label={label}
            value={value}
            onChange={(v) => onUpdate({ [p.name]: v })}
            upstreamVars={upstreamVars}
            multiline={p.type === 'multiline'}
            rows={p.type === 'multiline' ? 5 : 3}
            placeholder={p.description ?? undefined}
          />
        );
      })}
    </>
  );
}
