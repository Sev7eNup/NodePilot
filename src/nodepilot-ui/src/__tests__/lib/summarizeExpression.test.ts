import { describe, it, expect } from 'vitest';
import { summarizeExpression } from '../../lib/summarizeExpression';
import type { ExprNode } from '../../components/designer/ConditionBuilder';

const RESOLVE = (id: string) => id; // identity resolver — the stepId IS the label

describe('summarizeExpression', () => {
  it('nullOrUndefinedExpression_returnsEmptyString', () => {
    expect(summarizeExpression(null)).toBe('');
    expect(summarizeExpression(undefined)).toBe('');
  });

  it('binaryComparison_literalEquality_quotesValue', () => {
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'step1', field: 'param', paramName: 'env' },
      op: '==',
      right: { kind: 'literal', value: 'prod' },
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('step1.env == "prod"');
  });

  it('binaryComparison_numberLiteral_isNotQuoted', () => {
    // Numeric literals stay unquoted so the summary reads naturally as a math expression.
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'count' },
      op: '<',
      right: { kind: 'literal', value: '10' },
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.count < 10');
  });

  it('unaryComparison_omitsRightOperand', () => {
    // isEmpty/isNotEmpty/isTrue/isFalse don't take a right operand. The summary must
    // not render a phantom "?" for it.
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'value' },
      op: 'isEmpty',
      right: undefined,
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.value is empty');
  });

  it('comparisonOperators_useShortSymbols', () => {
    // ≤/≥ instead of <=/>= — keeps the labels visually compact in the canvas.
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'age' },
      op: '<=',
      right: { kind: 'literal', value: '18' },
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.age ≤ 18');
  });

  it('matchesOperator_usesTilde', () => {
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'email' },
      op: 'matches',
      right: { kind: 'literal', value: '^[a-z]+@example.com$' },
    };

    // Truncate threshold is 15 chars; ^[a-z]+@example is exactly 15 → keep + ellipsis.
    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.email ~ "^[a-z]+@example…"');
  });

  it('longLiteral_isTruncatedWithEllipsis', () => {
    // 15+ chars → truncated. Keeps edge-label real-estate predictable on the canvas.
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'output', paramName: '' },
      op: '==',
      right: { kind: 'literal', value: 'this is a long literal value' },
    };

    const result = summarizeExpression(expr, RESOLVE);
    expect(result).toContain('"this is a long …"');
    expect(result).not.toContain('long literal value');
  });

  it('emptyLiteral_renderedAsEmptyQuotes', () => {
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'output', paramName: '' },
      op: '==',
      right: { kind: 'literal', value: '' },
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.output == ""');
  });

  it('notNode_wrapsChildInParens', () => {
    const expr: ExprNode = {
      type: 'not',
      child: {
        type: 'comparison',
        left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'isDev' },
        op: 'isTrue',
        right: undefined,
      },
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('NOT (s1.isDev is true)');
  });

  it('groupAnd_joinsWithAnd', () => {
    const expr: ExprNode = {
      type: 'group',
      op: 'AND',
      children: [
        {
          type: 'comparison',
          left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'env' },
          op: '==',
          right: { kind: 'literal', value: 'prod' },
        },
        {
          type: 'comparison',
          left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'debug' },
          op: 'isFalse',
          right: undefined,
        },
      ],
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.env == "prod" AND s1.debug is false');
  });

  it('groupSingleChild_unwrapped_noOperatorRedundancy', () => {
    const expr: ExprNode = {
      type: 'group',
      op: 'AND',
      children: [
        {
          type: 'comparison',
          left: { kind: 'variable', stepId: 's1', field: 'output', paramName: '' },
          op: '==',
          right: { kind: 'literal', value: 'ok' },
        },
      ],
    };

    expect(summarizeExpression(expr, RESOLVE)).toBe('s1.output == "ok"');
  });

  it('emptyGroup_returnsEmptyMarker', () => {
    const expr: ExprNode = { type: 'group', op: 'AND', children: [] };
    expect(summarizeExpression(expr, RESOLVE)).toBe('(empty)');
  });

  it('nestedGroup_isParenthesized', () => {
    // Outer OR with two children, second child is a nested AND group → must render
    // parens around the AND so reading order matches semantics.
    const expr: ExprNode = {
      type: 'group',
      op: 'OR',
      children: [
        {
          type: 'comparison',
          left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'a' },
          op: '==',
          right: { kind: 'literal', value: 'x' },
        },
        {
          type: 'group',
          op: 'AND',
          children: [
            {
              type: 'comparison',
              left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'b' },
              op: '==',
              right: { kind: 'literal', value: 'y' },
            },
            {
              type: 'comparison',
              left: { kind: 'variable', stepId: 's1', field: 'param', paramName: 'c' },
              op: '==',
              right: { kind: 'literal', value: 'z' },
            },
          ],
        },
      ],
    };

    const result = summarizeExpression(expr, RESOLVE);
    expect(result).toBe('s1.a == "x" OR (s1.b == "y" AND s1.c == "z")');
  });

  it('stepLabelResolver_truncatesLongLabels', () => {
    // Step labels >14 chars get …-truncated to keep the summary on a single edge label.
    const expr: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 's1', field: 'output', paramName: '' },
      op: '==',
      right: { kind: 'literal', value: 'ok' },
    };
    const longLabelResolver = (id: string) => `${id}-very-long-step-label`;

    const result = summarizeExpression(expr, longLabelResolver);
    expect(result).toContain('s1-very-long-s…');
    expect(result).not.toContain('very-long-step-label');
  });
});
