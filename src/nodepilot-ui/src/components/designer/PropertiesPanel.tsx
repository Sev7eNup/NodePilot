import {
  Add,
  Checkmark,
  CheckmarkFilled,
  Chemistry,
  ChevronDown,
  ChevronRight,
  CircleDash,
  ErrorFilled,
  FlashFilled,
} from '@carbon/icons-react';
import { useState, useRef, useMemo, memo, createElement } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Node, Edge } from '@xyflow/react';
import type { ManagedMachine, Credential, WorkflowExecution, StepExecution } from '../../types/api';
import { getUpstreamVariables } from '../../lib/upstreamVariables';
import { tryParseJson } from '../../lib/jsonPathBuilder';
import { parseOutputParametersJson } from '../../lib/outputParameters';
import { setVariableDragData } from '../../lib/variableDragDrop';
import { ACTIVITY_TYPES, EXTERNAL_TRIGGER_TYPES } from '../../lib/activityTypes';
import {
  REMOTE_ACTIVITY_TYPES, TIMEOUT_ACTIVITY_TYPES,
  TimeoutField, DynamicTargetField,
} from './properties/shared';
import { PanelHeader, StatusPillRow, Section, FieldGrid } from './properties/panelChrome';
import { getActivityConfigComponent } from './properties/activityConfigMap';
import { isCustomActivityType, getCustomActivityFacts } from '../../lib/customActivities';
import { useVariableAutocomplete } from './properties/useVariableAutocomplete';
import { VariableSuggestionsDropdown } from './properties/VariableSuggestionsDropdown';
import { VariablePreviewTooltip } from './properties/VariablePreviewTooltip';
import { CloneConfigButton } from './properties/CloneConfigButton';
import { StepTestPanel } from './properties/StepTestPanel';
import { JsonPathTree } from './properties/JsonPathTree';
import { useDesignStore } from '../../stores/designStore';

// Re-export truth-tables so existing import sites keep working.
export { REMOTE_ACTIVITY_TYPES, TIMEOUT_ACTIVITY_TYPES };

interface Props {
  node: Node;
  allNodes: Node[];
  edges: Edge[];
  machines: ManagedMachine[];
  credentials: Credential[];
  onUpdate: (nodeId: string, data: Record<string, unknown>) => void;
  onClose: () => void;
  width?: number;
  workflowId?: string;
  /** Optional hook so the Start-Workflow config can pop the left-sidebar "Workflows" tab open. */
  onOpenWorkflowPicker?: () => void;
  /** Called on hover over a variable row — passes the producer node ID (or null to clear). */
  onVarHover?: (producerNodeId: string | null) => void;
  /** When false, all edit fields are disabled (wrapped in a fieldset). Default true. */
  canWrite?: boolean;
}

function PropertiesPanelImpl({
  node, allNodes, edges, machines, credentials, onUpdate, onClose, width, workflowId, onOpenWorkflowPicker, onVarHover,
  canWrite = true,
}: Readonly<Props>) {
  const { t } = useTranslation(['properties', 'common']);
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  const data = node.data as Record<string, unknown>;
  const activityType = (data.activityType as string) || ACTIVITY_TYPES.RUN_SCRIPT;
  const config = (data.config as Record<string, unknown>) || {};
  const label = (data.label as string) || '';
  const outputVariable = (data.outputVariable as string) || '';
  const description = (data.description as string) ?? '';
  const isDisabled = (data.disabled as boolean) === true;
  const hasBreakpoint = (data.breakpoint as boolean) === true;
  const breakpointCondition = (data.breakpointCondition as string) || '';
  const isExternalTrigger = EXTERNAL_TRIGGER_TYPES.includes(activityType as typeof EXTERNAL_TRIGGER_TYPES[number]);
  const isTrigger = isExternalTrigger || activityType === ACTIVITY_TYPES.MANUAL_TRIGGER;

  const upstreamVars = getUpstreamVariables(node.id, allNodes, edges);

  const updateData = (patch: Record<string, unknown>) => {
    onUpdate(node.id, { ...data, ...patch });
  };

  const updateConfig = (patch: Record<string, unknown>) => {
    updateData({ config: { ...config, ...patch } });
  };

  // Lazy-render description: empty stays as a "+ Add description" link until clicked.
  const [showDescription, setShowDescription] = useState(description !== '');

  // All step executions from the most recent terminal run. Powers two surfaces from a single
  // network call: the LastOutputBlock for the *selected* step, and the hover-preview tooltip
  // for *upstream variables* in AvailableVariablesList. Done in one query (rather than
  // per-step) so we don't fan out N requests on a 40-node workflow.
  const { data: lastStepsByWorkflow } = useQuery({
    queryKey: ['last-execution-steps', workflowId],
    enabled: !!workflowId,
    refetchInterval: 15_000,
    staleTime: 10_000,
    queryFn: async () => {
      const executions = await api.get<WorkflowExecution[]>(`/executions?workflowId=${workflowId}`);
      const last = executions.find((e) => ['Succeeded', 'Failed', 'Cancelled'].includes(e.status));
      if (!last) return null;
      return await api.get<StepExecution[]>(`/executions/${last.id}/steps`);
    },
  });
  const lastStepsByStepId = useMemo(() => {
    const map = new Map<string, StepExecution>();
    if (lastStepsByWorkflow) for (const s of lastStepsByWorkflow) map.set(s.stepId, s);
    return map;
  }, [lastStepsByWorkflow]);
  const lastStepOutput = isTrigger ? null : lastStepsByStepId.get(node.id) ?? null;

  const [showExprTester, setShowExprTester] = useState(false);

  // Custom activities expose their remote/timeout facts via the runtime catalog (not the static sets).
  const customFacts = isCustomActivityType(activityType) ? getCustomActivityFacts(activityType) : undefined;
  const showExecCtx = REMOTE_ACTIVITY_TYPES.has(activityType) || customFacts?.runsRemote === true;
  // startProgram / startWorkflow: timeout applies only in their "wait" mode — engine
  // ignores it in fire-and-forget. Hide the section in that case to avoid suggesting it works.
  const showTimeout = TIMEOUT_ACTIVITY_TYPES.has(activityType)
    || isCustomActivityType(activityType) // custom activities always run a script
    || (activityType === ACTIVITY_TYPES.START_PROGRAM && config.waitForExit !== false)
    || (activityType === ACTIVITY_TYPES.START_WORKFLOW && config.waitForCompletion !== false);
  // Stable module-level reference from ACTIVITY_CONFIG_COMPONENTS — rendered via createElement so the
  // react-hooks/static-components rule doesn't misread the dynamic dispatch as a per-render component.
  const configComponent = getActivityConfigComponent(activityType);

  const statusRow = (
    <StatusPillRow
      showExpertControls={expertMode}
      isDisabled={isDisabled}
      hasBreakpoint={hasBreakpoint}
      breakpointCondition={breakpointCondition}
      outputVariable={outputVariable}
      outputVariablePlaceholder={node.id}
      upstreamVars={upstreamVars}
      onToggleDisabled={() => updateData({ disabled: !isDisabled })}
      onToggleBreakpoint={() => updateData({ breakpoint: !hasBreakpoint })}
      onChangeBreakpointCondition={(v) => updateData({ breakpointCondition: v })}
      onChangeOutputVariable={(v) => updateData({ outputVariable: v })}
    />
  );

  return (
    <div
      className="np-anim-panel bg-surface-low flex flex-col h-full overflow-hidden shrink-0 z-10 shadow-[-20px_0_40px_rgba(0,0,0,0.02)]"
      style={{ width: width ?? 450 }}
    >
      <div
        className="flex-1 overflow-y-auto px-6 pb-6 pt-0 space-y-3 @container"
        style={{ containerType: 'inline-size' }}
      >
        <PanelHeader
          activityType={activityType}
          name={label}
          onChangeName={(v) => updateData({ label: v })}
          onClose={onClose}
          statusRow={statusRow}
          canWrite={canWrite}
        />

        {/* Read-only wrap: a single fieldset block disables all <input>/<textarea>/<select>/<button>
            elements in the sections + activity configs below at once — without each of the ~26
            config components having to accept a `disabled` prop individually. `className="contents"`
            neutralizes the fieldset tag's default layout. */}
        {/* space-y-3 deliberately sits HERE (not only on the scroll container): its
            space-y selector only targets direct DOM children — thanks to `display: contents`,
            the sections stay children of the fieldset. */}
        <fieldset disabled={!canWrite} className="contents space-y-3">

        {/* Config-Clone affordance — sits at the top so it's the first thing the user sees
            when starting a new step that mirrors an existing one. Self-hides when no
            cloneable candidates exist. */}
        {expertMode && !isTrigger && (
          <CloneConfigButton
            currentNode={node}
            allNodes={allNodes}
            onClone={(patchedData) => onUpdate(node.id, patchedData)}
          />
        )}

        {showDescription ? (
          <textarea
            value={description}
            onChange={(e) => updateData({ description: e.target.value })}
            onBlur={() => { if (description === '') setShowDescription(false); }}
            className="input-field resize-none text-sm"
            rows={2}
            placeholder={t('stepDescriptionPlaceholder')}
            autoFocus={description === ''}
          />
        ) : (
          <button
            type="button"
            onClick={() => setShowDescription(true)}
            className="inline-flex items-center gap-1.5 px-2 py-1 -mx-2 rounded text-[11px] font-label text-on-surface-variant hover:text-primary hover:bg-primary-fixed/40 transition-colors"
          >
            <Add size={12} />
            {t('addDescription')}
          </button>
        )}

        {showExecCtx && (
          <Section title={t('executionContext')}>
            <FieldGrid>
              <DynamicTargetField
                label={t('properties:targetMachine')}
                value={(data.targetMachineId as string) || ''}
                onChange={(v) => updateData({ targetMachineId: v || null })}
                options={machines.map((m) => ({ id: m.id, label: `${m.name} (${m.hostname})` }))}
                placeholder="GUID, {{var}}, localhost"
                upstreamVars={upstreamVars}
                emptyLabel={t('properties:machineEmptyHint')}
                optionPickerLabel={t('properties:selectMachine')}
              />
              <DynamicTargetField
                label={t('properties:credential')}
                value={(data.credentialId as string) || ''}
                onChange={(v) => updateData({ credentialId: v || null })}
                options={credentials.map((c) => ({ id: c.id, label: `${c.name} (${c.username})` }))}
                placeholder="GUID, {{var}}"
                upstreamVars={upstreamVars}
                emptyLabel={t('properties:credentialEmptyHint')}
                optionPickerLabel={t('properties:selectCredential')}
              />
            </FieldGrid>
            <CredentialTestButton
              machineId={(data.targetMachineId as string) || ''}
              credentialId={(data.credentialId as string) || ''}
            />
          </Section>
        )}

        {upstreamVars.length > 0 && (
          <Section
            title={t('properties:inputVariables')}
            collapsible
            defaultOpen={false}
            action={
              <span className="font-label text-[9px] text-outline tabular-nums">
                {upstreamVars.length}
              </span>
            }
          >
            <AvailableVariablesList
              upstreamVars={upstreamVars}
              onVarHover={onVarHover}
              lastStepsByStepId={lastStepsByStepId}
            />
          </Section>
        )}

        {isExternalTrigger && (
          <div className="border border-outline-variant/30 bg-surface-container rounded-md p-2.5 text-[11px] font-label text-on-surface-variant leading-snug">
            <span className="inline-block w-2 h-2 rounded-full bg-emerald-400 shadow-[0_0_6px] shadow-emerald-500/60 mr-1.5" />
            <strong className="text-on-surface">{t('properties:triggerActiveTitle', { defaultValue: 'Active:' })}</strong>{' '}
            {t('properties:triggerActiveDescription', { defaultValue: 'This trigger is monitored automatically by TriggerOrchestrator. Changes take effect ~5s after saving. The workflow must be set to Enabled.' })}
          </div>
        )}

        {configComponent && (
          <Section title={t('properties:configuration')}>
            {createElement(configComponent, {
              config,
              onUpdate: updateConfig,
              upstreamVars,
              onOpenWorkflowPicker,
              workflowId,
              stepId: node.id,
              outputVariableName: outputVariable || undefined,
              lastStepsByStepId,
              isLocalTarget: !(data.targetMachineId),
            })}
          </Section>
        )}

        {showTimeout && (
          <Section
            title={t('properties:timeoutSection')}
            collapsible
            defaultOpen={false}
          >
            <TimeoutField
              value={config.timeoutSeconds as number | undefined}
              onChange={(v) => updateConfig({ timeoutSeconds: v })}
            />
          </Section>
        )}

        {workflowId && !isTrigger && (
          <Section
            title={expertMode ? t('properties:test.section') : t('properties:test.standardSection')}
            collapsible
            defaultOpen={false}
          >
            <StepTestPanel
              workflowId={workflowId}
              stepId={node.id}
              liveConfig={config}
              canRun={canWrite}
              expertMode={expertMode}
            />

            {lastStepOutput && <LastOutputBlock step={lastStepOutput} />}

            {expertMode && <div>
              <button
                type="button"
                onClick={() => setShowExprTester((v) => !v)}
                className="flex items-center gap-1.5 text-xs font-label font-semibold text-on-surface-variant hover:text-on-surface transition-colors"
              >
                <Chemistry size={12} />
                Expression Tester
                {showExprTester ? <ChevronDown size={11} /> : <ChevronRight size={11} />}
              </button>
              {showExprTester && (
                <ExpressionTester
                  upstreamVars={upstreamVars}
                  lastStepsByStepId={lastStepsByStepId}
                />
              )}
            </div>}
          </Section>
        )}

        </fieldset>
      </div>
    </div>
  );
}

/**
 * A memo with a targeted comparator: without it, PropertiesPanel would reconcile on every
 * `useDesignStore` tick in the WorkflowEditorPage parent (snap toggle, view-tier switch,
 * node-style change, lint refresh, dragging another node). At 583 lines including nested
 * `Section`s and `*Config` components, that's noticeable.
 *
 * The data props are compared (`node`, `allNodes`, `edges`, `machines`, `credentials`, `width`,
 * `workflowId`, `canWrite`). Inline closures from the editor (`onUpdate`, `onClose`,
 * `onOpenWorkflowPicker`, `onVarHover`) are deliberately excluded — they're semantically stable
 * (they only call state setters), but every page render creates new references. Comparing them
 * would effectively disable the memo.
 */
export const PropertiesPanel = memo(PropertiesPanelImpl, (prev, next) => {
  return prev.node === next.node
    && prev.allNodes === next.allNodes
    && prev.edges === next.edges
    && prev.machines === next.machines
    && prev.credentials === next.credentials
    && prev.width === next.width
    && prev.workflowId === next.workflowId
    && prev.canWrite === next.canWrite;
});

/**
 * Inner content of the Available-Input-Variables list. The Section wrapper
 * provides the card + collapse, so this only renders the grouped list.
 */
function AvailableVariablesList({ upstreamVars, onVarHover, lastStepsByStepId }: Readonly<{
  upstreamVars: ReturnType<typeof getUpstreamVariables>;
  onVarHover?: (producerNodeId: string | null) => void;
  /** Map of stepId → last terminal-run StepExecution. Powers the hover-preview tooltip. */
  lastStepsByStepId?: Map<string, StepExecution>;
}>) {
  const { t } = useTranslation(['common', 'designer']);
  const [copiedExpr, setCopiedExpr] = useState<string | null>(null);
  const byStep = new Map<string, { stepId: string; items: typeof upstreamVars }>();
  for (const v of upstreamVars) {
    const baseLabel = v.label.split(' → ')[0];
    if (!byStep.has(baseLabel)) byStep.set(baseLabel, { stepId: v.stepId, items: [] });
    byStep.get(baseLabel)!.items.push(v);
  }
  return (
    <div className="space-y-2">
      <div className="space-y-2 max-h-[240px] overflow-y-auto pr-1">
        {[...byStep.entries()].map(([step, { stepId, items }]) => (
          <div key={step}>
            <div className="text-[9px] font-label font-bold text-outline uppercase tracking-widest mb-1">{step}</div>
            <div className="space-y-0.5">
              {items.map((v) => (
                <VariablePreviewTooltip
                  key={v.expression}
                  step={lastStepsByStepId?.get(stepId)}
                  expression={v.expression}
                  title={`Click to copy ${v.expression}`}
                  onMouseEnter={() => onVarHover?.(stepId)}
                  onMouseLeave={() => onVarHover?.(null)}
                  onClick={() => {
                    navigator.clipboard.writeText(v.expression).catch(() => {});
                    setCopiedExpr(v.expression);
                    setTimeout(() => setCopiedExpr((c) => c === v.expression ? null : c), 1200);
                  }}
                >
                  <div
                    draggable
                    onDragStart={(e) => setVariableDragData(e, v.expression)}
                    className="flex items-center justify-between gap-2 rounded px-1 -mx-1 hover:bg-surface-high transition-colors cursor-pointer"
                  >
                    <code className={`text-[11px] px-1.5 py-0.5 rounded font-mono truncate transition-colors ${
                      copiedExpr === v.expression ? 'bg-success-container text-on-success-container' : 'bg-primary-fixed text-primary'
                    }`}>
                      {copiedExpr === v.expression
                        ? <span className="inline-flex items-center gap-1"><Checkmark size={11} /> {t('copied')}</span>
                        : v.expression}
                    </code>
                    <div className="flex items-center gap-1 shrink-0">
                      {v.type && v.type !== 'string' && (
                        <span className="text-[9px] font-bold px-1 py-0.5 rounded bg-surface-high text-on-surface-variant uppercase tracking-wide">
                          {v.type === 'number' ? 'num' : v.type === 'boolean' ? 'bool' : v.type === 'array' ? 'arr' : v.type === 'object' ? 'obj' : v.type}
                        </span>
                      )}
                      <span className="text-[10px] text-on-surface-variant truncate">{v.label.split(' → ')[1] ?? ''}</span>
                    </div>
                  </div>
                </VariablePreviewTooltip>
              ))}
            </div>
          </div>
        ))}
      </div>
      <p className="text-[10px] text-on-surface-variant pt-1">
        {t('designer:variablesInsertHint')}
      </p>
    </div>
  );
}

/* ---- Last Output Block — inline variant for the Test & Debug section ---- */

function LastOutputBlock({ step }: Readonly<{ step: StepExecution }>) {
  const { t } = useTranslation('properties');
  const [open, setOpen] = useState(false);
  const [copiedPath, setCopiedPath] = useState<string | null>(null);
  const parsedOutput = useMemo(() => tryParseJson(step.output), [step.output]);
  const copyJsonPath = (path: string) => {
    navigator.clipboard.writeText(path).catch(() => {});
    setCopiedPath(path);
    setTimeout(() => setCopiedPath((current) => current === path ? null : current), 1200);
  };
  const duration = step.startedAt && step.completedAt
    ? ((new Date(step.completedAt).getTime() - new Date(step.startedAt).getTime()) / 1000).toFixed(1) + 's'
    : null;
  const statusColor = step.status === 'Succeeded' ? 'text-emerald-700 dark:text-emerald-400' : step.status === 'Failed' ? 'text-error' : 'text-on-surface-variant';
  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center gap-1.5 px-2 py-1.5 text-xs font-label font-semibold hover:bg-surface-high rounded transition-colors"
      >
        {open ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
        <span className={statusColor}>{t('lastOutput')}</span>
        <span className={`ml-1 font-normal ${statusColor}`}>{step.status}</span>
        {duration && <span className="ml-auto font-normal text-on-surface-variant">{duration}</span>}
      </button>
      {open && (
        <div className="mt-1 px-1 space-y-1.5">
          {step.output && (
            <pre className="text-xs bg-surface-low rounded p-2 overflow-auto max-h-40 whitespace-pre-wrap font-mono text-on-surface border border-outline-variant/30">
              {step.output}
            </pre>
          )}
          {parsedOutput !== null && (
            <div className="space-y-1">
              <div className="flex items-center justify-between gap-2">
                <span className="text-[10px] font-label font-bold uppercase tracking-widest text-on-surface-variant">
                  JSONPath
                </span>
                {copiedPath && <code className="text-[10px] font-mono text-emerald-700 dark:text-emerald-400 truncate">{copiedPath}</code>}
              </div>
              <JsonPathTree value={parsedOutput} onPick={copyJsonPath} />
            </div>
          )}
          {step.errorOutput && (
            <pre className="text-xs bg-error/5 rounded p-2 overflow-auto max-h-32 whitespace-pre-wrap font-mono text-error border border-error/30">
              {step.errorOutput}
            </pre>
          )}
          {!step.output && !step.errorOutput && (
            <p className="text-xs text-on-surface-variant italic">{t('noOutputCaptured')}</p>
          )}
        </div>
      )}
    </div>
  );
}

/* ---- Expression Tester ---- */

const PLACEHOLDER_RE = /\{\{([\w.-]+)\}\}/g;

function ExpressionTester({
  upstreamVars,
  lastStepsByStepId,
}: Readonly<{
  upstreamVars: ReturnType<typeof getUpstreamVariables>;
  /**
   * Per-step lookup of the last terminal StepExecution. The prefill heuristic uses this
   * to resolve `{{head.field}}` against the producing step — pre-fix the tester only
   * had the currently-selected step's lastStep and would happily prefill `{{webApi.output}}`
   * with the Disk-Check stdout when that was the selected step.
   */
  lastStepsByStepId: Map<string, StepExecution>;
}>) {
  const [template, setTemplate] = useState('');
  const [mockValues, setMockValues] = useState<Record<string, string>>({});
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const autocomplete = useVariableAutocomplete({
    inputRef: textareaRef,
    value: template,
    onChange: setTemplate,
    upstreamVars,
  });

  // Variable-head → producing stepId. `upstreamVars[i].variable` looks like
  // `diskCheck.output` or `diskCheck.param.host`; the head before the first dot is what
  // a `{{diskCheck.output}}` template references. Multiple entries can share the same
  // head — the first one wins (their stepId is identical anyway).
  const headToStepId = useMemo(() => {
    const map = new Map<string, string>();
    for (const v of upstreamVars) {
      const head = v.variable.split('.')[0];
      if (head && !map.has(head)) map.set(head, v.stepId);
    }
    return map;
  }, [upstreamVars]);

  const placeholders = [...new Set([...template.matchAll(PLACEHOLDER_RE)].map((m) => m[1]))];

  const prefill = (key: string): string => {
    if (mockValues[key] !== undefined) return mockValues[key];
    const dotIdx = key.indexOf('.');
    if (dotIdx < 0) return '';
    const head = key.slice(0, dotIdx);
    const field = key.slice(dotIdx + 1);
    const stepId = headToStepId.get(head) ?? head; // fall back to literal stepId
    const producingStep = lastStepsByStepId.get(stepId);
    if (!producingStep) return '';
    if (field === 'output') return producingStep.output ?? '';
    if (field === 'error' || field === 'errorOutput') return producingStep.errorOutput ?? '';
    if (field.startsWith('param.')) {
      const map = parseOutputParametersJson(producingStep.outputParametersJson);
      const paramName = field.slice('param.'.length);
      if (map && Object.prototype.hasOwnProperty.call(map, paramName)) {
        return map[paramName];
      }
    }
    return '';
  };

  const resolved = template.replace(PLACEHOLDER_RE, (_, key) => {
    const val = mockValues[key] ?? prefill(key);
    return val !== '' ? val : `{{${key}}}`;
  });

  const hasUnresolved = /\{\{([\w.-]+)\}\}/.test(resolved);

  return (
    <div className="mt-2 space-y-2 bg-surface-low border border-outline-variant/30 rounded-md p-2.5">
      <div className="relative">
        <textarea
          ref={textareaRef}
          value={template}
          onChange={(e) => setTemplate(e.target.value)}
          onKeyUp={autocomplete.refresh}
          onSelect={autocomplete.refresh}
          onKeyDown={autocomplete.handleKeyDown}
          onBlur={() => setTimeout(autocomplete.close, 150)}
          placeholder={'Enter template, e.g. Server: {{disk.output}}'}
          className="w-full text-xs font-mono border border-outline-variant/30 rounded p-2 bg-surface-container resize-y min-h-[56px] focus:outline-none focus:ring-1 focus:ring-primary"
        />
        <VariableSuggestionsDropdown
          open={autocomplete.open}
          suggestions={autocomplete.filtered}
          selectedIdx={autocomplete.selectedIdx}
          onPick={autocomplete.pick}
          anchorRef={textareaRef}
        />
      </div>
      {placeholders.length > 0 && (
        <div className="space-y-1">
          {placeholders.map((key) => (
            <div key={key} className="flex items-center gap-2">
              <code className="text-[10px] bg-primary-fixed text-primary px-1.5 py-0.5 rounded font-mono shrink-0 max-w-[120px] truncate">
                {`{{${key}}}`}
              </code>
              <input
                className="flex-1 text-xs border border-outline-variant/30 rounded px-2 py-1 bg-surface-container font-mono focus:outline-none focus:ring-1 focus:ring-primary"
                placeholder="test value…"
                value={mockValues[key] ?? prefill(key)}
                onChange={(e) => setMockValues((prev) => ({ ...prev, [key]: e.target.value }))}
              />
            </div>
          ))}
        </div>
      )}
      {template && (
        <div className={`text-xs rounded p-2 font-mono whitespace-pre-wrap break-all ${hasUnresolved ? 'bg-amber-50 border border-amber-200 text-amber-800' : 'bg-surface-container border border-green-200 text-green-800'}`}>
          {resolved}
        </div>
      )}
      {upstreamVars.length > 0 && template === '' && (
        <div className="text-[10px] text-on-surface-variant">
          Verfügbare Variablen: {upstreamVars.slice(0, 4).map((v) => v.expression).join(', ')}
          {upstreamVars.length > 4 && ` +${upstreamVars.length - 4} mehr`}
        </div>
      )}
    </div>
  );
}

/* ---- Credential-Test-Button ---- */

/**
 * Calls `POST /api/machines/{id}/test` with optional credential override and shows
 * the result as an inline status badge next to the button. Blocking but short-lived
 * — 15 s server-side timeout. No side effect on `Machine.IsReachable` when the
 * override is used (otherwise a step-specific failed test could falsely flag a
 * healthy host as unreachable).
 */
function CredentialTestButton({ machineId, credentialId }: Readonly<{ machineId: string; credentialId: string }>) {
  const [state, setState] = useState<'idle' | 'running' | 'ok' | 'fail'>('idle');
  const [message, setMessage] = useState('');

  // Only offer when a real machine GUID is set (not a `{{var}}` expression). At design
  // time we can't know which machine a variable resolves to.
  const isRealGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(machineId);
  const credIsVariable = credentialId && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(credentialId);

  if (!isRealGuid) return null;

  const run = async () => {
    setState('running');
    setMessage('');
    try {
      const body = credentialId && !credIsVariable ? { credentialId } : {};
      const res = await api.post<{ success: boolean; computerName?: string; error?: string; credentialUsed?: string }>(
        `/machines/${machineId}/test`, body);
      if (res.success) {
        setState('ok');
        setMessage(`${res.credentialUsed ?? 'default'} → ${res.computerName?.trim() || 'reachable'}`);
      } else {
        setState('fail');
        setMessage(res.error || 'unknown failure');
      }
    } catch (e) {
      setState('fail');
      setMessage((e as Error).message);
    }
  };

  return (
    <div className="space-y-1">
      <button
        type="button"
        onClick={run}
        disabled={state === 'running'}
        className={`inline-flex items-center gap-2 px-2.5 py-1.5 rounded-md text-[11px] font-label font-semibold border transition-colors cursor-pointer ${
          state === 'running' ? 'bg-surface-high text-on-surface-variant border-outline-variant/30' :
          state === 'ok' ? 'bg-green-50 text-green-700 border-green-200 hover:bg-green-100' :
          state === 'fail' ? 'bg-red-50 text-red-700 border-red-200 hover:bg-red-100' :
          'bg-surface-high text-on-surface border-outline-variant/30 hover:bg-surface-highest'
        }`}
        title="Probiert eine kurze Verbindung zur Machine — verifiziert dass Host erreichbar + Credential gültig ist."
      >
        {state === 'running' ? <CircleDash size={12} className="animate-spin" /> :
         state === 'ok' ? <CheckmarkFilled size={12} /> :
         state === 'fail' ? <ErrorFilled size={12} /> :
         <FlashFilled size={12} />}
        {state === 'running' ? 'Testing…' :
         state === 'ok' ? 'Reachable' :
         state === 'fail' ? 'Failed' :
         'Test connection'}
      </button>
      {message && state !== 'running' && (
        <p className={`text-[10px] font-mono truncate ${state === 'ok' ? 'text-green-700' : 'text-error'}`} title={message}>
          {message}
        </p>
      )}
      {credIsVariable && (
        <p className="text-[10px] text-outline italic">
          (Credential ist eine Variable — Test verwendet den Machine-Default.)
        </p>
      )}
    </div>
  );
}
