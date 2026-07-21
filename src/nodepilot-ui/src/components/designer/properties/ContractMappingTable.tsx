import { Information, WarningAltFilled, WarningFilled } from '@carbon/icons-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { VariableInsertField } from './shared';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';
import type { WorkflowContractResponse, WorkflowContractInput, WorkflowContractOutput } from '../../../types/api';

interface Props {
  contract: WorkflowContractResponse;
  /** Current parameter dict from the StartWorkflow node config. */
  values: Record<string, string>;
  onChange: (next: Record<string, string>) => void;
  upstreamVars: UpstreamVariable[];
  /** The parent step's outputVariable (or step-id fallback). Used to render the
   *  "available downstream as `{{<step>.param.x}}`" hint in the outputs section. */
  parentStepHandle: string;
}

/**
 * Typed mapping table for sub-workflow parameter wiring. Replaces the free-form
 * ParameterTable when the child workflow exposes a derivable contract.
 *
 * Behaviors aligned with the engine:
 * - Setting a value to "" REMOVES the key from the parameters dict (so the child's
 *   declared default kicks in). The engine treats `{ foo: "" }` as "user provided
 *   empty string, override the default" — typically not what the author intended.
 * - Required + missing is a validation error ONLY when there's no declared default.
 *   Required + has-default is fine: the child uses the default.
 * - Stale keys (in `values` but not in `contract.inputs`) are still rendered with a
 *   warning so the author can see "this is being sent but the child doesn't expect
 *   it". They are NOT auto-pruned — that would be a destructive surprise.
 */
export function ContractMappingTable({ contract, values, onChange, upstreamVars, parentStepHandle }: Readonly<Props>) {
  const { t } = useTranslation('properties');

  const inputsByName = useMemo(() => {
    const m = new Map<string, WorkflowContractInput>();
    for (const i of contract.inputs) m.set(i.name, i);
    return m;
  }, [contract.inputs]);

  const staleKeys = useMemo(
    () => Object.keys(values).filter((k) => !inputsByName.has(k)),
    [values, inputsByName],
  );

  const updateValue = (name: string, val: string) => {
    const next = { ...values };
    if (val === '') {
      // Empty out → drop the key so the child's declared default takes effect.
      delete next[name];
    } else {
      next[name] = val;
    }
    onChange(next);
  };

  const removeStale = (name: string) => {
    const next = { ...values };
    delete next[name];
    onChange(next);
  };

  // No declared inputs (workflow has no manualTrigger or manualTrigger has no params).
  // Author can still call the workflow, but they'd need to use the free-form
  // ParameterTable — render an info banner here. The free-form fallback lives in
  // the parent component (StartWorkflowConfig), not here.
  if (!contract.hasManualTrigger) {
    return (
      <div className="rounded-md border border-primary/30 bg-primary/10 p-3 space-y-1.5">
        <div className="flex items-center gap-1.5 text-xs font-label font-semibold text-primary">
          <Information size={13} />
          {t('config.startWorkflow.contract.noManualTriggerHeader')}
        </div>
        <p className="text-[11px] text-primary leading-snug">
          {t('config.startWorkflow.contract.noManualTriggerHint', { workflowName: contract.workflowName })}
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {/* Inputs section */}
      <div className="rounded-md border border-outline-variant bg-surface-low">
        <div className="px-2.5 py-1.5 border-b border-outline-variant text-[11px] font-label font-semibold text-on-surface-variant">
          {t('config.startWorkflow.contract.inputsHeader', { workflowName: contract.workflowName })}
        </div>
        <div className="p-2 space-y-2">
          {contract.inputs.length === 0 && (
            <div className="text-[11px] text-on-surface-variant py-1">
              {t('config.startWorkflow.contract.noInputs')}
            </div>
          )}
          {contract.inputs.map((input) => {
            const value = values[input.name] ?? '';
            const isMissing = !value && input.required && input.default === null;
            return (
              <InputRow
                key={input.name}
                input={input}
                value={value}
                isMissing={isMissing}
                upstreamVars={upstreamVars}
                onChange={(v) => updateValue(input.name, v)}
              />
            );
          })}
        </div>
      </div>
      {/* Stale-keys section — params author sends but child doesn't expect */}
      {staleKeys.length > 0 && (
        <div className="rounded-md border border-orange-200 bg-orange-50 p-2 space-y-1.5">
          <div className="flex items-center gap-1.5 text-[11px] font-label font-semibold text-orange-800">
            <WarningAltFilled size={12} />
            {t('config.startWorkflow.contract.staleKeysHeader')}
          </div>
          {staleKeys.map((key) => (
            <div key={key} className="flex items-center gap-2 text-[11px] font-mono">
              <span className="text-orange-900 flex-1 truncate">{key} = {values[key]}</span>
              <button
                type="button"
                onClick={() => removeStale(key)}
                className="text-orange-700 hover:text-orange-900 underline text-[10px]"
              >
                {t('config.startWorkflow.contract.removeStaleKey')}
              </button>
            </div>
          ))}
        </div>
      )}
      {/* Outputs section */}
      <OutputsSection
        outputs={contract.outputs}
        hasReturnData={contract.hasReturnData}
        hasMultipleReturnDataNodes={contract.hasMultipleReturnDataNodes}
        parentStepHandle={parentStepHandle}
      />
    </div>
  );
}

function InputRow({
  input, value, isMissing, upstreamVars, onChange,
}: Readonly<{
  input: WorkflowContractInput;
  value: string;
  isMissing: boolean;
  upstreamVars: UpstreamVariable[];
  onChange: (val: string) => void;
}>) {
  const { t } = useTranslation('properties');
  return (
    <div className="space-y-1">
      <div className="flex items-baseline gap-1.5">
        <span className="font-mono text-[12px] text-on-surface">{input.name}</span>
        {input.required && (
          <span
            className="text-red-600 text-[11px]"
            title={t('config.startWorkflow.contract.requiredTooltip')}
          >
            *
          </span>
        )}
        <span className="text-[10px] font-label uppercase tracking-wide text-on-surface-variant">
          {input.type}
        </span>
        {input.hasConflict && (
          <span
            className="inline-flex items-center gap-0.5 text-[10px] font-semibold text-amber-700 bg-amber-100 px-1.5 py-0.5 rounded"
            title={t('config.startWorkflow.contract.conflictTooltip')}
          >
            <WarningAltFilled size={10} />
            {t('config.startWorkflow.contract.conflictBadge')}
          </span>
        )}
        {input.default !== null && !value && (
          <span className="text-[10px] text-on-surface-variant ml-auto">
            {t('config.startWorkflow.contract.defaultHint', { default: input.default || '""' })}
          </span>
        )}
      </div>
      {input.description && (
        <div className="text-[10px] text-on-surface-variant leading-snug">{input.description}</div>
      )}
      <div className={isMissing ? 'rounded ring-1 ring-red-400' : undefined}>
        <VariableInsertField
          label=""
          value={value}
          onChange={onChange}
          upstreamVars={upstreamVars}
          compact
          mono
          placeholder={input.default ?? ''}
        />
      </div>
      {isMissing && (
        <div className="flex items-center gap-1 text-[10px] text-red-700">
          <WarningFilled size={10} />
          {t('config.startWorkflow.contract.requiredMissing')}
        </div>
      )}
    </div>
  );
}

function OutputsSection({
  outputs, hasReturnData, hasMultipleReturnDataNodes, parentStepHandle,
}: Readonly<{
  outputs: WorkflowContractOutput[];
  hasReturnData: boolean;
  hasMultipleReturnDataNodes: boolean;
  parentStepHandle: string;
}>) {
  const { t } = useTranslation('properties');

  return (
    <div className="rounded-md border border-outline-variant bg-surface-low">
      <div className="px-2.5 py-1.5 border-b border-outline-variant text-[11px] font-label font-semibold text-on-surface-variant flex items-center gap-1.5">
        {t('config.startWorkflow.contract.outputsHeader')}
        {hasMultipleReturnDataNodes && (
          <span
            className="inline-flex items-center gap-0.5 text-[10px] font-semibold text-amber-700 bg-amber-100 px-1.5 py-0.5 rounded ml-auto"
            title={t('config.startWorkflow.contract.multipleReturnDataTooltip')}
          >
            <WarningAltFilled size={10} />
            {t('config.startWorkflow.contract.multipleReturnDataBadge')}
          </span>
        )}
      </div>
      <div className="p-2 space-y-1">
        {outputs.map((out) => (
          <div key={out.name + ':' + out.source} className="flex items-baseline gap-2 text-[11px] font-mono">
            <span
              className={`${out.source === 'system' ? 'text-blue-700' : out.source === 'multiple' ? 'text-amber-700' : 'text-indigo-700'} flex-shrink-0`}
              title={out.source === 'system'
                ? t('config.startWorkflow.contract.systemOutputTooltip')
                : out.source === 'multiple'
                  ? t('config.startWorkflow.contract.multipleSourceTooltip')
                  : undefined}
            >
              {out.name}
            </span>
            <span className="text-on-surface-variant">→</span>
            <span className="text-on-surface truncate">
              {`{{${parentStepHandle}.param.${out.name}}}`}
            </span>
          </div>
        ))}
        {!hasReturnData && (
          <div className="text-[10px] text-on-surface-variant pt-1 italic">
            {t('config.startWorkflow.contract.noReturnDataHint')}
          </div>
        )}
      </div>
    </div>
  );
}
