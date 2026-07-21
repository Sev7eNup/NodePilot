import { Close } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { VariableInsertField } from './shared';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';

interface ParameterTableProps {
  /** Label for the section header (e.g. "Parameter", "Zusätzliche Parameter"). */
  label: string;
  /** Text for the add-row action; defaults to "+ Parameter". */
  addLabel?: string;
  /** Text rendered when no rows exist; shown in italics. */
  emptyMessage: string;
  /** Current map of key → value. */
  parameters: Record<string, string>;
  /** Called with the next map whenever the user adds, edits, or removes a row. */
  onChange: (next: Record<string, string>) => void;
  /** Upstream variables surfaced in the value-field's `{{` autocomplete. */
  upstreamVars?: UpstreamVariable[];
}

/**
 * Key/value editor used by the startWorkflow and forEach config panes. Each row is a pair
 * of <input>s (key, value) with a remove button; the add-button appends a blank row.
 *
 * Extracted from ForEachConfig/StartWorkflowConfig because both maintained an identical
 * handler triple (add / update / remove) and near-identical JSX. ReturnDataConfig has a
 * structurally different row (VariableInsertField for the value) and is not covered here.
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
  const entries = Object.entries(parameters);

  const addRow = () => onChange({ ...parameters, '': '' });

  const updateRow = (oldKey: string, newKey: string, value: string) => {
    const next: Record<string, string> = {};
    for (const [k, v] of entries) {
      if (k === oldKey) next[newKey] = value;
      else next[k] = v;
    }
    onChange(next);
  };

  const removeRow = (k: string) => {
    const next: Record<string, string> = {};
    for (const [key, val] of entries) if (key !== k) next[key] = val;
    onChange(next);
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-1">
        <label className="block text-xs font-semibold text-on-surface-variant uppercase tracking-wide">
          {label}
        </label>
        <button
          type="button"
          onClick={addRow}
          className="text-xs text-primary hover:underline font-medium"
        >
          {addLabel}
        </button>
      </div>
      {entries.length === 0 && (
        <p className="text-[11px] text-on-surface-variant italic">{emptyMessage}</p>
      )}
      {entries.map(([k, v], idx) => (
        <div key={idx} className="flex items-center gap-1.5 mb-1.5">
          <input
            type="text"
            value={k}
            onChange={(e) => updateRow(k, e.target.value, v)}
            placeholder={t('parameterTable.keyPlaceholder')}
            className="input-field !py-1 flex-[0_0_40%] text-sm font-mono"
          />
          <VariableInsertField
            label=""
            value={v}
            onChange={(nv: string) => updateRow(k, k, nv)}
            upstreamVars={upstreamVars}
            placeholder={t('parameterTable.valuePlaceholder')}
            compact
          />
          <button
            type="button"
            onClick={() => removeRow(k)}
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
