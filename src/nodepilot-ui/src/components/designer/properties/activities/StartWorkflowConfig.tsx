import { CircleDash, FolderTree, View } from '@carbon/icons-react';
import { useContext } from 'react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { ParameterTable } from '../ParameterTable';
import { ContractMappingTable } from '../ContractMappingTable';
import { useWorkflowContract } from '../../../../hooks/useWorkflowContract';
import { SubWorkflowPreviewContext } from '../../nodes/ActivityNode';

export function StartWorkflowConfig({ config, onUpdate, upstreamVars = [], onOpenWorkflowPicker, stepId, outputVariableName }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const workflowNameOrId = (config.workflowNameOrId as string) || '';
  const waitForCompletion = config.waitForCompletion !== false;
  const parameters = (config.parameters as Record<string, string>) || {};
  const { onPreviewSubWorkflow } = useContext(SubWorkflowPreviewContext);
  const { contract, isLoading, isVariableExpression, isNotFound } = useWorkflowContract(workflowNameOrId);
  // Resolve the handle the parent step exposes downstream — outputVariable wins, falls
  // back to step-id, falls back to a generic placeholder so the UI hint is still readable
  // even before the user names the step.
  const parentStepHandle = outputVariableName?.trim() || stepId || 'thisStep';

  const trimmed = workflowNameOrId.trim();
  const isPreviewable = !!trimmed && !trimmed.startsWith('{{');

  return (
    <>
      <div className="flex items-center gap-1.5">
        {onOpenWorkflowPicker && (
          <button
            type="button"
            onClick={onOpenWorkflowPicker}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-primary-fixed text-primary hover:bg-primary-fixed-dim text-xs font-label font-semibold transition-colors flex-1 justify-center"
            title="Workflow aus der Library auswählen"
          >
            <FolderTree size={13} />
            Aus Library wählen
          </button>
        )}
        <button
          type="button"
          onClick={() => onPreviewSubWorkflow(trimmed)}
          disabled={!isPreviewable}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-primary/10 text-primary border border-primary/30 hover:bg-primary/20 text-xs font-label font-semibold transition-colors disabled:opacity-40 disabled:cursor-not-allowed flex-1 justify-center"
          title={isPreviewable
            ? 'Sub-Workflow als read-only Vorschau anzeigen'
            : trimmed.startsWith('{{')
              ? 'Vorschau nicht möglich — Referenz ist eine Variable und löst sich erst zur Laufzeit auf'
              : 'Setze zuerst einen Workflow-Namen oder eine GUID'}
          data-testid="subworkflow-preview-button"
        >
          <View size={13} />
          Vorschau
        </button>
      </div>
      <VariableInsertField
        label="Workflow (Name oder GUID)"
        value={workflowNameOrId}
        onChange={(v) => onUpdate({ workflowNameOrId: v })}
        upstreamVars={upstreamVars}
        placeholder="z. B. Rollback-Runbook"
      />
      <Field label="Auf Abschluss warten">
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={waitForCompletion}
            onChange={(e) => onUpdate({ waitForCompletion: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1">
            <div className="text-sm font-medium text-on-surface">
              {waitForCompletion ? 'Synchron (warten)' : 'Fire-and-forget'}
            </div>
            <div className="text-[11px] text-on-surface-variant leading-snug">
              {waitForCompletion
                ? 'Dieser Step wartet bis der Child-Workflow fertig ist. Dessen returnData steht downstream zur Verfügung. Timeout greift unten.'
                : 'Child-Workflow wird angestoßen und läuft im Hintergrund weiter. Dieser Step succeedet sofort — nur executionId wird zurückgegeben.'}
            </div>
          </div>
        </label>
      </Field>
      {/* Contract-aware mapping when we successfully derived one; free-form table
          otherwise. The free-form fallback covers: empty input, variable-expression
          ({{...}}) input, lookup-in-flight, and unknown-workflow (404). Loading
          indicator above the fallback so the user sees we're trying. */}
      {isLoading && (
        <div className="flex items-center gap-1.5 text-[11px] text-on-surface-variant">
          <CircleDash size={11} className="animate-spin" />
          {t('config.startWorkflow.contract.loading')}
        </div>
      )}
      {contract ? (
        <ContractMappingTable
          contract={contract}
          values={parameters}
          onChange={(next) => onUpdate({ parameters: next })}
          upstreamVars={upstreamVars}
          parentStepHandle={parentStepHandle}
        />
      ) : (
        <>
          {isNotFound && (
            <div className="text-[11px] text-amber-700 bg-amber-50 border border-amber-200 rounded px-2 py-1.5">
              {t('config.startWorkflow.contract.workflowNotFound', { name: workflowNameOrId })}
            </div>
          )}
          {isVariableExpression && (
            <div className="text-[11px] text-on-surface-variant bg-surface-highest border border-outline-variant rounded px-2 py-1.5">
              {t('config.startWorkflow.contract.dynamicRefHint')}
            </div>
          )}
          <ParameterTable
            label="Parameter"
            emptyMessage="Keine Parameter. Der Child-Workflow erhält nur den call-depth-Zähler."
            parameters={parameters}
            onChange={(next) => onUpdate({ parameters: next })}
            upstreamVars={upstreamVars}
          />
        </>
      )}
    </>
  );
}
