import type { ExprNode, ExprOperand, ComparisonOp } from '../components/designer/ConditionBuilder';

const UNARY: ReadonlySet<ComparisonOp> = new Set<ComparisonOp>([
  'isEmpty', 'isNotEmpty', 'isTrue', 'isFalse',
]);

const OP_SHORT: Record<ComparisonOp, string> = {
  '==': '==', '!=': '!=', '<': '<', '>': '>', '<=': '≤', '>=': '≥',
  contains: 'contains', startsWith: 'starts', endsWith: 'ends', matches: '~',
  isEmpty: 'is empty', isNotEmpty: 'is not empty', isTrue: 'is true', isFalse: 'is false',
};

export type StepLabelResolver = (stepId: string) => string;

function formatOperand(op: ExprOperand, resolve: StepLabelResolver): string {
  if (op.kind === 'literal') {
    const v = op.value ?? '';
    if (v === '') return '""';
    if (/^-?\d+(\.\d+)?$/.test(v)) return v;
    const trimmed = v.length > 15 ? v.slice(0, 15) + '…' : v;
    return `"${trimmed}"`;
  }
  // kind === 'variable'. Discriminate further by source — global/manual carry a flat `name`,
  // step (the default) carries stepId + field + optional paramName.
  if (op.source === 'global') return `globals.${op.name}`;
  if (op.source === 'manual') return `manual.${op.name}`;
  if (op.source === 'event') return `event.${op.name}`;
  const stepLabel = resolve(op.stepId);
  const short = stepLabel.length > 14 ? stepLabel.slice(0, 14) + '…' : stepLabel;
  const fieldPart = op.field === 'param' ? `.${op.paramName ?? '?'}` : `.${op.field}`;
  return short + fieldPart;
}

export function summarizeExpression(
  expr: ExprNode | null | undefined,
  resolve: StepLabelResolver = (id) => id,
): string {
  if (!expr) return '';

  const format = (node: ExprNode): string => {
    if (node.type === 'comparison') {
      const left = formatOperand(node.left, resolve);
      const opText = OP_SHORT[node.op] ?? node.op;
      if (UNARY.has(node.op)) return `${left} ${opText}`;
      const right = node.right ? formatOperand(node.right, resolve) : '?';
      return `${left} ${opText} ${right}`;
    }
    if (node.type === 'not') {
      return `NOT (${format(node.child)})`;
    }
    // group
    if (node.children.length === 0) return '(empty)';
    if (node.children.length === 1) return format(node.children[0]);
    const parts = node.children.map((c) => {
      const str = format(c);
      // Nested groups are wrapped in parentheses so the structure stays readable.
      return c.type === 'group' && c.children.length > 1 ? `(${str})` : str;
    });
    return parts.join(` ${node.op} `);
  };

  return format(expr);
}
