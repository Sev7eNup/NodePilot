import { useTranslation } from 'react-i18next';
import { Close } from '@carbon/icons-react';
import { VariableInsertField, type ConfigProps } from '../shared';

export function ReturnDataConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const data = (config.data as Record<string, string>) || {};
  const entries = Object.entries(data);

  const setData = (next: Record<string, string>) => onUpdate({ data: next });
  const addKey = () => setData({ ...data, '': '' });
  const updateKey = (oldKey: string, newKey: string, value: string) => {
    const next: Record<string, string> = {};
    for (const [k, v] of entries) {
      if (k === oldKey) next[newKey] = value;
      else next[k] = v;
    }
    setData(next);
  };
  const removeKey = (k: string) => {
    const next: Record<string, string> = {};
    for (const [key, val] of entries) if (key !== k) next[key] = val;
    setData(next);
  };

  return (
    <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-semibold text-on-surface-variant uppercase tracking-wide">
            {t('returnData.returnFields')}
          </label>
          <button
            type="button"
            onClick={addKey}
            className="text-xs text-primary hover:underline font-medium"
          >
            {t('returnData.addField')}
          </button>
        </div>
        {entries.length === 0 && (
          <p className="text-[11px] text-on-surface-variant italic">
            {t('returnData.noReturnFields')}
          </p>
        )}
        {entries.map(([k, v], idx) => (
          <div key={idx} className="mb-2">
            <div className="flex items-center gap-1.5 mb-1">
              <input
                type="text"
                value={k}
                onChange={(e) => updateKey(k, e.target.value, v)}
                placeholder={t('parameterTable.keyPlaceholder')}
                className="input-field !py-1 flex-[0_0_40%] text-sm font-mono"
              />
              <button
                type="button"
                onClick={() => removeKey(k)}
                className="text-error hover:text-error-high text-sm px-1"
                title={t('parameterTable.remove')}
              >
                <Close size={14} />
              </button>
            </div>
            <VariableInsertField
              label=""
              value={v}
              onChange={(nv) => updateKey(k, k, nv)}
              upstreamVars={upstreamVars}
              placeholder={t('parameterTable.valuePlaceholderUpstream')}
            />
          </div>
        ))}
    </div>
  );
}
