import { Add, ArrowDown, ArrowUp, TrashCan } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { Field, type ConfigProps } from '../shared';
import { ConditionBuilder, type ExprNode } from '../../ConditionBuilder';

interface DecisionCase {
  name: string;
  condition: ExprNode | null;
}

export function DecisionConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const cases: DecisionCase[] = Array.isArray(config.cases)
    ? (config.cases as DecisionCase[])
    : [];
  const defaultCaseName = (config.defaultCaseName as string) || 'default';

  const setCases = (next: DecisionCase[]) => onUpdate({ cases: next });

  const addCase = () => {
    setCases([...cases, { name: `case${cases.length + 1}`, condition: null }]);
  };

  const updateCase = (idx: number, patch: Partial<DecisionCase>) => {
    const next = cases.slice();
    next[idx] = { ...next[idx], ...patch };
    setCases(next);
  };

  const removeCase = (idx: number) => {
    setCases(cases.filter((_, i) => i !== idx));
  };

  const moveCase = (idx: number, direction: -1 | 1) => {
    const target = idx + direction;
    if (target < 0 || target >= cases.length) return;
    const next = cases.slice();
    [next[idx], next[target]] = [next[target], next[idx]];
    setCases(next);
  };

  return (
    <>
      <div className="text-[11px] text-on-surface-variant leading-snug">
        {t('config.decision.caseHelpPrefix')}{' '}
        <code className="bg-surface-high px-1 rounded">{`{{step.param.case}}`}</code>.
        {' '}{t('config.decision.caseHelpMiddle')}{' '}
        <code className="bg-surface-high px-1 rounded">step.param.case == 'name'</code>
        {' '}{t('config.decision.caseHelpSuffix')}
      </div>
      {cases.map((c, idx) => (
        <div key={idx} className="rounded-md border border-outline-variant/40 bg-surface-high/50 p-2 space-y-2">
          <div className="flex items-center gap-1.5">
            <span className="text-[10px] font-mono text-outline tabular-nums w-5 text-right">{idx + 1}</span>
            <input
              type="text"
              value={c.name}
              onChange={(e) => updateCase(idx, { name: e.target.value })}
              className="input-field flex-1 font-mono text-sm"
              placeholder={t('config.decision.caseNamePlaceholder')}
            />
            <button
              type="button"
              onClick={() => moveCase(idx, -1)}
              disabled={idx === 0}
              className="p-1 rounded hover:bg-surface-highest text-on-surface-variant disabled:opacity-30"
              title={t('config.decision.moveUp')}
            >
              <ArrowUp size={14} />
            </button>
            <button
              type="button"
              onClick={() => moveCase(idx, 1)}
              disabled={idx === cases.length - 1}
              className="p-1 rounded hover:bg-surface-highest text-on-surface-variant disabled:opacity-30"
              title={t('config.decision.moveDown')}
            >
              <ArrowDown size={14} />
            </button>
            <button
              type="button"
              onClick={() => removeCase(idx)}
              className="p-1 rounded hover:bg-error/10 text-error"
              title={t('config.decision.deleteCase')}
            >
              <TrashCan size={14} />
            </button>
          </div>
          <ConditionBuilder
            value={c.condition}
            upstreamVars={upstreamVars}
            onChange={(next) => updateCase(idx, { condition: next })}
          />
        </div>
      ))}
      <button
        type="button"
        onClick={addCase}
        className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-md bg-primary-fixed text-primary hover:bg-primary-fixed-dim text-xs font-label font-semibold transition-colors w-full"
      >
        <Add size={14} />
        {t('config.decision.addCase')}
      </button>
      <Field label={t('config.decision.defaultCaseName')}>
        <input
          type="text"
          value={defaultCaseName}
          onChange={(e) => onUpdate({ defaultCaseName: e.target.value })}
          className="input-field font-mono text-sm"
          placeholder="default"
        />
      </Field>
    </>
  );
}
