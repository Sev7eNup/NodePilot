import { Close } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { VariableInsertField } from './shared';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';
import { useKeyValueRows } from '../../../hooks/useKeyValueRows';

interface ParameterTableProps {
  /** Label for the section header, for example "Parameters". */
  label: string;
  /** Text for the add-row action; defaults to "+ Parameter". */
  addLabel?: string;
  /** Text rendered when no rows exist; shown in italics. */
  emptyMessage: string;
  /** Current key/value map. */
  parameters: Record<string, string>;
  /** Called with the next map whenever the user adds, edits, or removes a row. */
  onChange: (next: Record<string, string>) => void;
  /** Upstream variables surfaced in the value-field's `{{` autocomplete. */
  upstreamVars?: UpstreamVariable[];
}

/**
 * Key/value editor used by the startWorkflow and forEach config panes. Each row is a pair
 * of <input>s (key, value) with a remove button; the add button appends a blank row.
 * ReturnDataConfig uses a structurally different row and is not covered here.
 */
export function ParameterTable({
  label,
  addLabel = '+ Parameter',
  emptyMessage,
  parameters,
  onChange,
  upstreamVars = [],
}: Readonly<ParameterTableProps>) {
  const { t } = useTranslation('properties');
  const { rows, add, rename, setValue, remove } = useKeyValueRows(parameters, onChange);

  return (
    <div>
      <div className="flex items-center justify-between mb-1">
        <label className="block text-xs font-semibold text-on-surface-variant uppercase tracking-wide">
          {label}
        </label>
        <button
          type="button"
          onClick={add}
          className="text-xs text-primary hover:underline font-medium"
        >
          {addLabel}
        </button>
      </div>
      {rows.length === 0 && (
        <p className="text-[11px] text-on-surface-variant italic">{emptyMessage}</p>
      )}
      {rows.map((row) => (
        <div key={row.id} className="flex items-center gap-1.5 mb-1.5">
          <input
            type="text"
            value={row.key}
            onChange={(e) => rename(row.id, e.target.value)}
            placeholder={t('parameterTable.keyPlaceholder')}
            aria-invalid={row.duplicate || undefined}
            title={row.duplicate ? t('parameterTable.duplicateName') : undefined}
            className={`input-field !py-1 flex-[0_0_40%] text-sm font-mono${row.duplicate ? ' !border-error' : ''}`}
          />
          <VariableInsertField
            label=""
            value={row.value}
            onChange={(nv: string) => setValue(row.id, nv)}
            upstreamVars={upstreamVars}
            placeholder={t('parameterTable.valuePlaceholder')}
            compact
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
      ))}
    </div>
  );
}
