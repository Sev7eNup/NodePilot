import { useTranslation } from 'react-i18next';
import { Close } from '@carbon/icons-react';
import { VariableInsertField, type ConfigProps } from '../shared';
import { useKeyValueRows } from '../../../../hooks/useKeyValueRows';

export function ReturnDataConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const data = (config.data as Record<string, string>) || {};
  const { rows, add, rename, setValue, remove } = useKeyValueRows(data, (next) => onUpdate({ data: next }));

  return (
    <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-semibold text-on-surface-variant uppercase tracking-wide">
            {t('returnData.returnFields')}
          </label>
          <button
            type="button"
            onClick={add}
            className="text-xs text-primary hover:underline font-medium"
          >
            {t('returnData.addField')}
          </button>
        </div>
        {rows.length === 0 && (
          <p className="text-[11px] text-on-surface-variant italic">
            {t('returnData.noReturnFields')}
          </p>
        )}
        {rows.map((row) => (
          <div key={row.id} className="mb-2">
            <div className="flex items-center gap-1.5 mb-1">
              <input
                type="text"
                value={row.key}
                onChange={(e) => rename(row.id, e.target.value)}
                placeholder={t('parameterTable.keyPlaceholder')}
                aria-invalid={row.duplicate || undefined}
                title={row.duplicate ? t('parameterTable.duplicateName') : undefined}
                className={`input-field !py-1 flex-[0_0_40%] text-sm font-mono${row.duplicate ? ' !border-error' : ''}`}
              />
              <button
                type="button"
                onClick={() => remove(row.id)}
                className="text-error hover:text-error-high text-sm px-1"
                title={t('parameterTable.remove')}
              >
                <Close size={14} />
              </button>
            </div>
            <VariableInsertField
              label=""
              value={row.value}
              onChange={(nv) => setValue(row.id, nv)}
              upstreamVars={upstreamVars}
              placeholder={t('parameterTable.valuePlaceholderUpstream')}
            />
          </div>
        ))}
    </div>
  );
}
