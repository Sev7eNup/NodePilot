import { Add, MisuseOutline, TrashCan } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { Trans, useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { UpstreamVariable } from '../../lib/upstreamVariables';

/* -------------------------- Expression AST types --------------------------- */

/**
 * Operand discriminated by `kind` (literal vs variable) and — for variables — by an
 * optional `source` discriminator. Step variables omit `source` (default), global and
 * manual variables carry `source: 'global' | 'manual'` and a flat `name`. The engine
 * (`ConditionEvaluator.ResolveOperand`) reads the same shape.
 */
export type ExprOperand =
  | { kind: 'variable'; source?: 'step'; stepId: string; field: 'output' | 'error' | 'success' | 'param'; paramName?: string }
  | { kind: 'variable'; source: 'global'; name: string }
  | { kind: 'variable'; source: 'manual'; name: string }
  | { kind: 'variable'; source: 'event'; name: string }
  | { kind: 'literal'; value: string };

/**
 * An event-field option for the alerting filter mode. When `eventFields` is passed to
 * <see cref="ConditionBuilder"/>, operands can reference `source: 'event'` fields from this
 * catalog instead of upstream step outputs. `name` must match a key from the backend
 * NotificationContext.ToFieldMap(); `label` is the human-readable dropdown text.
 */
export interface EventFieldOption {
  name: string;
  label: string;
}

export type ExprComparison = {
  type: 'comparison';
  left: ExprOperand;
  op: ComparisonOp;
  right?: ExprOperand;
};

export type ExprGroup = {
  type: 'group';
  op: 'AND' | 'OR';
  children: ExprNode[];
};

export type ExprNot = {
  type: 'not';
  child: ExprNode;
};

export type ExprNode = ExprComparison | ExprGroup | ExprNot;

export type ComparisonOp =
  | '==' | '!=' | '<' | '>' | '<=' | '>='
  | 'contains' | 'startsWith' | 'endsWith' | 'matches'
  | 'isEmpty' | 'isNotEmpty' | 'isTrue' | 'isFalse';

const UNARY_OPS = new Set<ComparisonOp>(['isEmpty', 'isNotEmpty', 'isTrue', 'isFalse']);

/* ------------------------- Step+Field helpers ------------------------------ */

interface StepEntry {
  stepId: string;
  label: string;
  paramNames: string[];
  /** Output variable of the step; the engine accepts it as `stepId` too. */
  alias?: string;
}

function collectSteps(upstreamVars: UpstreamVariable[]): StepEntry[] {
  const byStep = new Map<string, StepEntry>();
  for (const v of upstreamVars) {
    const existing = byStep.get(v.stepId);
    const isParam = v.variable.includes('.param.');
    const baseLabel = isParam ? v.label.split(' → ')[0] : v.label;
    if (!existing) {
      byStep.set(v.stepId, { stepId: v.stepId, label: baseLabel, paramNames: [] });
    }
    if (!isParam && v.variable !== v.stepId) byStep.get(v.stepId)!.alias = v.variable;
    if (isParam) {
      const pName = v.variable.split('.param.')[1];
      byStep.get(v.stepId)!.paramNames.push(pName);
    }
  }
  return [...byStep.values()];
}

/* --------------------------- Operand Picker -------------------------------- */

type Field = 'output' | 'error' | 'success' | 'param';

function OperandPicker({
  operand, steps, eventFields, onChange,
}: Readonly<{ operand: ExprOperand; steps: StepEntry[]; eventFields?: EventFieldOption[]; onChange: (o: ExprOperand) => void }>) {
  const { t } = useTranslation('designer');
  const isVariable = operand.kind === 'variable';
  const hasVariableSource = steps.length > 0 || (eventFields?.length ?? 0) > 0;

  const toggleMode = (mode: 'variable' | 'literal') => {
    // Clicking the active mode must not replace the operand.
    if ((mode === 'variable') === isVariable) return;
    if (mode === 'literal') onChange({ kind: 'literal', value: '' });
    else if (eventFields && eventFields.length > 0) {
      onChange({ kind: 'variable', source: 'event', name: eventFields[0].name });
    } else {
      const first = steps[0];
      if (first) onChange({ kind: 'variable', stepId: first.stepId, field: 'output' });
      else onChange({ kind: 'literal', value: '' });
    }
  };

  return (
    <div className="flex-1 min-w-[140px] flex flex-col gap-1">
      <div className="flex gap-0.5 text-[10px]">
        <button
          onClick={() => toggleMode('variable')}
          disabled={!hasVariableSource}
          className={`px-2 py-1 rounded-md font-medium transition-colors ${isVariable ? 'bg-primary text-on-primary' : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'} disabled:opacity-50`}
          title={!hasVariableSource ? t('condition.noUpstreamSteps') : t('condition.referenceStep')}
        >{eventFields ? t('condition.fieldMode') : t('condition.variable')}</button>
        <button
          onClick={() => toggleMode('literal')}
          className={`px-2 py-1 rounded-md font-medium transition-colors ${!isVariable ? 'bg-primary text-on-primary' : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'}`}
          title={t('condition.useFixedValue')}
        >{t('condition.literal')}</button>
      </div>
      {isVariable ? (
        <VariableOperandInput operand={operand} steps={steps} eventFields={eventFields} onChange={onChange} />
      ) : (
        <input
          type="text"
          value={operand.kind === 'literal' ? operand.value : ''}
          onChange={(e) => onChange({ kind: 'literal', value: e.target.value })}
          placeholder={t('condition.literalPlaceholder')}
          className="text-xs bg-surface-high border border-transparent focus:border-primary/40 rounded-md px-2 py-1.5 font-mono outline-none transition-colors"
        />
      )}
    </div>
  );
}

type GlobalVarRow = { id: string; name: string; description: string | null };

type VariableOperand = Extract<ExprOperand, { kind: 'variable' }>;
type OperandOption = { key: string; label: string; operand: VariableOperand };
type OperandGroup = { id: string; label: string; options: OperandOption[] };

function stepOperand(stepId: string, field: Field, paramName?: string): VariableOperand {
  return field === 'param' ? { kind: 'variable', stepId, field, paramName } : { kind: 'variable', stepId, field };
}

// Select value of an operand. An output-variable alias resolves to the node id, so it selects
// the same option as a reference by node id.
function operandKey(operand: VariableOperand, aliasToStepId: ReadonlyMap<string, string>): string {
  if (operand.source === 'global' || operand.source === 'manual' || operand.source === 'event') {
    return `${operand.source}|${operand.name}`;
  }
  const stepId = aliasToStepId.get(operand.stepId) ?? operand.stepId;
  return operand.field === 'param'
    ? `step|${stepId}|param|${operand.paramName ?? ''}`
    : `step|${stepId}|${operand.field}`;
}

function operandReference(operand: VariableOperand): string {
  if (operand.source === 'global') return `globals.${operand.name}`;
  if (operand.source === 'manual') return `manual.${operand.name}`;
  if (operand.source === 'event') return operand.name;
  return operand.field === 'param'
    ? `${operand.stepId}.param.${operand.paramName ?? ''}`
    : `${operand.stepId}.${operand.field}`;
}

function VariableOperandInput({
  operand, steps, eventFields, onChange,
}: Readonly<{
  operand: VariableOperand;
  steps: StepEntry[];
  eventFields?: EventFieldOption[];
  onChange: (o: ExprOperand) => void;
}>) {
  const eventMode = (eventFields?.length ?? 0) > 0;
  // Globals are fetched from the /global-variables API; staleTime is high because the
  // set rarely changes and the builder typically gets rendered many times per editor session.
  const { t } = useTranslation('designer');
  // In alerting/event mode there are no step/global operands, so the query is disabled.
  const { data: globals = [], isSuccess: globalsLoaded } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVarRow[]>('/global-variables'),
    staleTime: 60_000,
    enabled: !eventMode,
  });

  const groups: OperandGroup[] = [];
  if (eventMode) {
    groups.push({
      id: 'event',
      label: t('condition.groupEvent'),
      options: eventFields!.map((f) => ({
        key: `event|${f.name}`, label: f.label, operand: { kind: 'variable', source: 'event', name: f.name },
      })),
    });
  }
  for (const s of steps) {
    groups.push({
      id: `step|${s.stepId}`,
      label: s.label,
      options: [
        { key: `step|${s.stepId}|output`, label: t('condition.outputText', { label: s.label }), operand: stepOperand(s.stepId, 'output') },
        { key: `step|${s.stepId}|error`, label: t('condition.errorText', { label: s.label }), operand: stepOperand(s.stepId, 'error') },
        { key: `step|${s.stepId}|success`, label: t('condition.successBool', { label: s.label }), operand: stepOperand(s.stepId, 'success') },
        ...s.paramNames.map((p) => ({
          key: `step|${s.stepId}|param|${p}`,
          label: t('condition.paramOption', { label: s.label, param: p }),
          operand: stepOperand(s.stepId, 'param', p),
        })),
      ],
    });
  }
  const globalOptions: OperandOption[] = globals.map((g) => ({
    key: `global|${g.name}`,
    label: t('condition.globalsOption', { name: g.name }),
    operand: { kind: 'variable', source: 'global', name: g.name },
  }));

  const aliasToStepId = new Map(steps.flatMap((s) => (s.alias ? [[s.alias, s.stepId] as const] : [])));
  const currentKey = operandKey(operand, aliasToStepId);
  const known = [...groups.flatMap((g) => g.options), ...globalOptions].some((o) => o.key === currentKey);
  // A reference the lists do not offer stays selectable as itself, so the condition is shown and
  // saved unchanged. Trigger inputs and globals that are still loading are valid; the rest is stale.
  const currentOption: OperandOption = { key: currentKey, label: operandReference(operand), operand };
  let stale = false;
  if (!known) {
    if (operand.source === 'manual') {
      groups.push({ id: 'manual', label: t('condition.groupManual'), options: [currentOption] });
    } else if (operand.source === 'global' && !globalsLoaded && !eventMode) {
      globalOptions.push(currentOption);
    } else {
      stale = true;
      groups.unshift({
        id: 'unavailable',
        label: t('condition.groupUnavailable'),
        options: [{ ...currentOption, label: t('condition.unavailableOption', { ref: currentOption.label }) }],
      });
    }
  }
  if (globalOptions.length > 0) groups.push({ id: 'globals', label: t('condition.groupGlobals'), options: globalOptions });

  const handleChange = (key: string) => {
    const match = groups.flatMap((g) => g.options).find((o) => o.key === key);
    if (match) onChange(match.operand);
  };

  return (
    <>
      <select
        value={currentKey}
        onChange={(e) => handleChange(e.target.value)}
        aria-invalid={stale || undefined}
        className={`text-xs bg-surface-high border ${stale ? 'border-error/60' : 'border-transparent'} focus:border-primary/40 rounded-md px-2 py-1.5 truncate outline-none transition-colors`}
      >
        {groups.map((g) => (
          <optgroup key={g.id} label={g.label}>
            {g.options.map((o) => (
              <option key={o.key} value={o.key}>{o.label}</option>
            ))}
          </optgroup>
        ))}
      </select>
      {stale && <p className="text-[10px] text-error leading-snug">{t('condition.unavailableHint')}</p>}
    </>
  );
}

// i18n key suffix per operator (designer:condition.ops.*); symbols are not valid readable keys.
const OP_KEYS: Record<ComparisonOp, string> = {
  '==': 'eq', '!=': 'ne', '<': 'lt', '>': 'gt', '<=': 'le', '>=': 'ge',
  contains: 'contains', startsWith: 'startsWith', endsWith: 'endsWith', matches: 'matches',
  isEmpty: 'isEmpty', isNotEmpty: 'isNotEmpty', isTrue: 'isTrue', isFalse: 'isFalse',
};

/* ---------------------------- Main Component ------------------------------- */

interface BuilderProps {
  value: ExprNode | null;
  upstreamVars: UpstreamVariable[];
  onChange: (next: ExprNode | null) => void;
  /**
   * When provided, the builder runs in alerting "event filter" mode: operands reference these
   * event fields (`source: 'event'`) instead of upstream step outputs. Leave undefined for the
   * designer's edge-condition use (steps + globals) — fully backward-compatible.
   */
  eventFields?: EventFieldOption[];
}

export function ConditionBuilder({ value, upstreamVars, onChange, eventFields }: Readonly<BuilderProps>) {
  const { t } = useTranslation('designer');
  const root: ExprGroup =
    value?.type === 'group'
      ? value
      : value
        ? { type: 'group', op: 'AND', children: [value] }
        : { type: 'group', op: 'AND', children: [] };

  const updateRoot = (next: ExprGroup) => {
    if (next.children.length === 0) onChange(null);
    else if (next.children.length === 1 && next.op === 'AND') onChange(next.children[0]);
    else onChange(next);
  };

  return (
    <div className="space-y-2">
      <p className="text-[11px] text-on-surface-variant leading-snug">
        <Trans
          t={t}
          i18nKey={eventFields ? 'condition.introEvent' : 'condition.intro'}
          components={[<span key="0" className="font-semibold text-primary" />, <span key="1" className="font-semibold text-primary" />]}
        />
      </p>
      <GroupNode node={root} upstreamVars={upstreamVars} eventFields={eventFields} onChange={updateRoot} isRoot />
      {root.children.length === 0 && (
        <p className="text-xs text-outline">{t(eventFields ? 'condition.noFilterSet' : 'condition.noConditionSet')}</p>
      )}
    </div>
  );
}

/* ------------------------------ Group Node --------------------------------- */

function GroupNode({
  node, upstreamVars, eventFields, onChange, isRoot,
}: Readonly<{ node: ExprGroup; upstreamVars: UpstreamVariable[]; eventFields?: EventFieldOption[]; onChange: (n: ExprGroup) => void; isRoot?: boolean }>) {
  const { t } = useTranslation('designer');
  const defaultComparison = (): ExprComparison => {
    const defaultLeft: ExprOperand =
      eventFields && eventFields.length > 0
        ? { kind: 'variable', source: 'event', name: eventFields[0].name }
        : upstreamVars[0]
          ? { kind: 'variable', stepId: upstreamVars[0].stepId, field: 'output' }
          : { kind: 'literal', value: '' };
    return {
      type: 'comparison',
      left: defaultLeft,
      op: '==',
      right: { kind: 'literal', value: '' },
    };
  };
  const addComparison = () => {
    onChange({ ...node, children: [...node.children, defaultComparison()] });
  };
  const addGroup = () => {
    onChange({ ...node, children: [...node.children, { type: 'group', op: 'AND', children: [] }] });
  };
  const addNot = () => {
    onChange({ ...node, children: [...node.children, { type: 'not', child: defaultComparison() }] });
  };
  const removeChild = (idx: number) => {
    onChange({ ...node, children: node.children.filter((_, i) => i !== idx) });
  };
  const updateChild = (idx: number, next: ExprNode) => {
    onChange({ ...node, children: node.children.map((c, i) => (i === idx ? next : c)) });
  };

  return (
    <div className={`rounded-md border ${isRoot ? 'border-transparent' : 'border-primary/30 bg-primary/10 p-2'} space-y-2`}>
      {node.children.length > 1 && (
        <div className="inline-flex items-center rounded-md bg-surface-high p-0.5 gap-0.5">
          <button
            onClick={() => onChange({ ...node, op: 'AND' })}
            className={`px-2.5 py-1 text-xs rounded font-semibold transition-colors ${node.op === 'AND' ? 'bg-primary text-on-primary shadow-sm' : 'text-on-surface-variant hover:text-on-surface'}`}
          >{t('condition.and')}</button>
          <button
            onClick={() => onChange({ ...node, op: 'OR' })}
            className={`px-2.5 py-1 text-xs rounded font-semibold transition-colors ${node.op === 'OR' ? 'bg-primary text-on-primary shadow-sm' : 'text-on-surface-variant hover:text-on-surface'}`}
          >{t('condition.or')}</button>
        </div>
      )}
      {node.children.map((child, idx) => (
        <div key={idx} className="flex items-start gap-1">
          <div className="flex-1">
            <ExprNodeView node={child} upstreamVars={upstreamVars} eventFields={eventFields} onChange={(n) => updateChild(idx, n)} />
          </div>
          <button
            onClick={() => removeChild(idx)}
            className="text-outline hover:text-error p-1"
            title={t('condition.remove')}
          ><TrashCan size={14} /></button>
        </div>
      ))}
      <div className="flex gap-1">
        <button
          onClick={addComparison}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-primary/10 text-primary border border-primary/30 rounded hover:bg-primary/20"
        ><Add size={12} /> {t('condition.addCondition')}</button>
        {isRoot && (
          <button
            onClick={addGroup}
            className="flex items-center gap-1 px-2 py-1 text-xs bg-surface-low text-on-surface border border-outline-variant/50 rounded hover:bg-surface-container"
          ><Add size={12} /> {t('condition.addGroup')}</button>
        )}
        <button
          onClick={addNot}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-surface-low text-on-surface border border-outline-variant/50 rounded hover:bg-surface-container"
        ><MisuseOutline size={12} /> {t('condition.not')}</button>
      </div>
    </div>
  );
}

/* ------------------------------ Any Node ----------------------------------- */

// Renders every node shape, so not(group) and not(not(...)) stay visible and editable.
function ExprNodeView({
  node, upstreamVars, eventFields, onChange,
}: Readonly<{ node: ExprNode; upstreamVars: UpstreamVariable[]; eventFields?: EventFieldOption[]; onChange: (n: ExprNode) => void }>) {
  const { t } = useTranslation('designer');
  if (node.type === 'comparison') {
    return <ComparisonRow node={node} upstreamVars={upstreamVars} eventFields={eventFields} onChange={onChange} />;
  }
  if (node.type === 'group') {
    return <GroupNode node={node} upstreamVars={upstreamVars} eventFields={eventFields} onChange={onChange} />;
  }
  return (
    <div className="pl-2 border-l-2 border-error/40">
      <div className="text-[10px] font-semibold text-error mb-1">{t('condition.not')}</div>
      <ExprNodeView
        node={node.child}
        upstreamVars={upstreamVars}
        eventFields={eventFields}
        onChange={(n) => onChange({ type: 'not', child: n })}
      />
    </div>
  );
}

/* --------------------------- Comparison Row -------------------------------- */

function ComparisonRow({
  node, upstreamVars, eventFields, onChange,
}: Readonly<{ node: ExprComparison; upstreamVars: UpstreamVariable[]; eventFields?: EventFieldOption[]; onChange: (n: ExprComparison) => void }>) {
  const { t } = useTranslation('designer');
  const steps = collectSteps(upstreamVars);

  const setOp = (op: ComparisonOp) => {
    if (UNARY_OPS.has(op)) {
      onChange({ ...node, op, right: undefined });
    } else {
      onChange({ ...node, op, right: node.right ?? { kind: 'literal', value: '' } });
    }
  };

  return (
    <div className="flex flex-wrap gap-2 items-start bg-surface-lowest border border-outline-variant/30 rounded-lg p-2.5 shadow-[var(--np-elev-1)]">
      <OperandPicker
        operand={node.left}
        steps={steps}
        eventFields={eventFields}
        onChange={(left) => onChange({ ...node, left })}
      />

      <select
        value={node.op}
        onChange={(e) => setOp(e.target.value as ComparisonOp)}
        className="text-xs bg-surface-high border border-transparent focus:border-primary/40 rounded-md px-2 py-1.5 self-center outline-none transition-colors"
      >
        <optgroup label={t('condition.opGroupCompare')}>
          {(['==','!=','<','>','<=','>='] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{t(`condition.ops.${OP_KEYS[o]}`)}</option>
          ))}
        </optgroup>
        <optgroup label={t('condition.opGroupString')}>
          {(['contains','startsWith','endsWith','matches'] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{t(`condition.ops.${OP_KEYS[o]}`)}</option>
          ))}
        </optgroup>
        <optgroup label={t('condition.opGroupUnary')}>
          {(['isEmpty','isNotEmpty','isTrue','isFalse'] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{t(`condition.ops.${OP_KEYS[o]}`)}</option>
          ))}
        </optgroup>
      </select>

      {!UNARY_OPS.has(node.op) && (
        <OperandPicker
          operand={node.right ?? { kind: 'literal', value: '' }}
          steps={steps}
          eventFields={eventFields}
          onChange={(right) => onChange({ ...node, right })}
        />
      )}
    </div>
  );
}
