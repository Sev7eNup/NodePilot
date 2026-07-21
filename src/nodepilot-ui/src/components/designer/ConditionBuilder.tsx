import { Add, MisuseOutline, TrashCan } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
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
 * catalog instead of upstream step outputs. `name` MUST match a key from the backend
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
        >{eventFields ? t('condition.fieldMode', 'Field') : 'Variable'}</button>
        <button
          onClick={() => toggleMode('literal')}
          className={`px-2 py-1 rounded-md font-medium transition-colors ${!isVariable ? 'bg-primary text-on-primary' : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'}`}
          title={t('condition.useFixedValue')}
        >Literal</button>
      </div>
      {isVariable ? (
        <VariableOperandInput operand={operand} steps={steps} eventFields={eventFields} onChange={onChange} />
      ) : (
        <input
          type="text"
          value={operand.kind === 'literal' ? operand.value : ''}
          onChange={(e) => onChange({ kind: 'literal', value: e.target.value })}
          placeholder="fixed value or {{globals.X}}"
          className="text-xs bg-surface-high border border-transparent focus:border-primary/40 rounded-md px-2 py-1.5 font-mono outline-none transition-colors"
        />
      )}
    </div>
  );
}

type GlobalVarRow = { id: string; name: string; description: string | null };

function VariableOperandInput({
  operand, steps, eventFields, onChange,
}: Readonly<{
  operand: Extract<ExprOperand, { kind: 'variable' }>;
  steps: StepEntry[];
  eventFields?: EventFieldOption[];
  onChange: (o: ExprOperand) => void;
}>) {
  const eventMode = (eventFields?.length ?? 0) > 0;
  // Globals are fetched from the /global-variables API; staleTime is high because the
  // set rarely changes and the builder typically gets rendered many times per editor session.
  // In alerting/event mode there are no step/global operands, so the query is disabled.
  const { data: globals = [] } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVarRow[]>('/global-variables'),
    staleTime: 60_000,
    enabled: !eventMode,
  });

  // Flat key encoding mirrors the operand schema:
  //   step:   "step|<stepId>|<field>" or "step|<stepId>|param|<paramName>"
  //   global: "global|<name>"
  //   manual: "manual|<name>"
  //   event:  "event|<name>"
  const currentKey = (() => {
    if (operand.source === 'global') return `global|${operand.name}`;
    if (operand.source === 'manual') return `manual|${operand.name}`;
    if (operand.source === 'event') return `event|${operand.name}`;
    // Step-source (default). TypeScript narrows the union to the step-shape variant here.
    return operand.field === 'param' && operand.paramName
      ? `step|${operand.stepId}|param|${operand.paramName}`
      : `step|${operand.stepId}|${operand.field}`;
  })();

  const handleChange = (key: string) => {
    const parts = key.split('|');
    const tag = parts[0];
    if (tag === 'global') {
      onChange({ kind: 'variable', source: 'global', name: parts[1] ?? '' });
      return;
    }
    if (tag === 'event') {
      onChange({ kind: 'variable', source: 'event', name: parts[1] ?? '' });
      return;
    }
    // tag === 'step'
    const stepId = parts[1];
    const field = parts[2] as Field;
    if (field === 'param' && parts[3]) {
      onChange({ kind: 'variable', stepId, field: 'param', paramName: parts[3] });
    } else {
      onChange({ kind: 'variable', stepId, field });
    }
  };

  return (
    <select
      value={currentKey}
      onChange={(e) => handleChange(e.target.value)}
      className="text-xs bg-surface-high border border-transparent focus:border-primary/40 rounded-md px-2 py-1.5 truncate outline-none transition-colors"
    >
      {eventMode && (
        <optgroup label="Event">
          {eventFields!.map((f) => (
            <option key={f.name} value={`event|${f.name}`}>{f.label}</option>
          ))}
        </optgroup>
      )}
      {steps.map((s) => (
        <optgroup key={s.stepId} label={s.label}>
          <option value={`step|${s.stepId}|output`}>{s.label} → output (text)</option>
          <option value={`step|${s.stepId}|error`}>{s.label} → error (text)</option>
          <option value={`step|${s.stepId}|success`}>{s.label} → success (bool)</option>
          {s.paramNames.map((p) => (
            <option key={p} value={`step|${s.stepId}|param|${p}`}>{s.label} → {p} (param)</option>
          ))}
        </optgroup>
      ))}
      {globals.length > 0 && (
        <optgroup label="Globals">
          {globals.map((g) => (
            <option key={g.id} value={`global|${g.name}`}>globals.{g.name}</option>
          ))}
        </optgroup>
      )}
    </select>
  );
}

const OP_LABELS: Record<ComparisonOp, string> = {
  '==': 'equals', '!=': 'not equals', '<': 'less than', '>': 'greater than', '<=': '≤', '>=': '≥',
  contains: 'contains', startsWith: 'starts with', endsWith: 'ends with', matches: 'matches regex',
  isEmpty: 'is empty', isNotEmpty: 'is not empty', isTrue: 'is true', isFalse: 'is false',
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
        {eventFields ? (
          <>Vergleiche Event-Felder mit einem festen Wert (oder untereinander). Auf beiden Seiten wählbar zwischen <span className="font-semibold text-primary">Field</span> und <span className="font-semibold text-primary">Literal</span>.</>
        ) : (
          <>Vergleiche Output-Werte vorheriger Steps miteinander oder mit einem festen Wert. Auf beiden Seiten kannst du zwischen <span className="font-semibold text-primary">Variable</span> und <span className="font-semibold text-primary">Literal</span> wählen.</>
        )}
      </p>
      <GroupNode node={root} upstreamVars={upstreamVars} eventFields={eventFields} onChange={updateRoot} isRoot />
      {root.children.length === 0 && (
        <p className="text-xs text-outline">{t('condition.noConditionSet')}</p>
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
          >AND</button>
          <button
            onClick={() => onChange({ ...node, op: 'OR' })}
            className={`px-2.5 py-1 text-xs rounded font-semibold transition-colors ${node.op === 'OR' ? 'bg-primary text-on-primary shadow-sm' : 'text-on-surface-variant hover:text-on-surface'}`}
          >OR</button>
        </div>
      )}
      {node.children.map((child, idx) => (
        <div key={idx} className="flex items-start gap-1">
          <div className="flex-1">
            {child.type === 'comparison' && (
              <ComparisonRow node={child} upstreamVars={upstreamVars} eventFields={eventFields} onChange={(n) => updateChild(idx, n)} />
            )}
            {child.type === 'group' && (
              <GroupNode node={child} upstreamVars={upstreamVars} eventFields={eventFields} onChange={(n) => updateChild(idx, n)} />
            )}
            {child.type === 'not' && (
              <div className="pl-2 border-l-2 border-error/40">
                <div className="text-[10px] font-semibold text-error mb-1">NOT</div>
                {child.child.type === 'comparison' && (
                  <ComparisonRow
                    node={child.child}
                    upstreamVars={upstreamVars}
                    eventFields={eventFields}
                    onChange={(n) => updateChild(idx, { type: 'not', child: n })}
                  />
                )}
              </div>
            )}
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
        ><Add size={12} /> Condition</button>
        {isRoot && (
          <button
            onClick={addGroup}
            className="flex items-center gap-1 px-2 py-1 text-xs bg-surface-low text-on-surface border border-outline-variant/50 rounded hover:bg-surface-container"
          ><Add size={12} /> Group</button>
        )}
        <button
          onClick={addNot}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-surface-low text-on-surface border border-outline-variant/50 rounded hover:bg-surface-container"
        ><MisuseOutline size={12} /> NOT</button>
      </div>
    </div>
  );
}

/* --------------------------- Comparison Row -------------------------------- */

function ComparisonRow({
  node, upstreamVars, eventFields, onChange,
}: Readonly<{ node: ExprComparison; upstreamVars: UpstreamVariable[]; eventFields?: EventFieldOption[]; onChange: (n: ExprComparison) => void }>) {
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
        <optgroup label="Compare">
          {(['==','!=','<','>','<=','>='] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{OP_LABELS[o]}</option>
          ))}
        </optgroup>
        <optgroup label="String">
          {(['contains','startsWith','endsWith','matches'] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{OP_LABELS[o]}</option>
          ))}
        </optgroup>
        <optgroup label="Unary">
          {(['isEmpty','isNotEmpty','isTrue','isFalse'] as ComparisonOp[]).map((o) => (
            <option key={o} value={o}>{OP_LABELS[o]}</option>
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
