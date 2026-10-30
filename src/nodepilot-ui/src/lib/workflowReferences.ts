/** Reference rewriting shared by authoring operations. Values outside reference-bearing
 * fields, including literal condition operands, retain their meaning and identity. */
export function rewriteTemplates(value: unknown, names: ReadonlyMap<string, string>): unknown {
  if (isRecord(value) && (value.type === 'comparison' || value.type === 'group' || value.type === 'not')) {
    return rewriteCondition(value, names);
  }
  if (typeof value === 'string') {
    return value.replace(/\{\{(\s*)([^}]+)\}\}/g, (token, space: string, body: string) => {
      // Longest first also handles imported aliases containing a dot, without cascading
      // one replacement into another when both names occur in the same fragment.
      for (const name of [...names.keys()].sort((a, b) => b.length - a.length)) {
        if (body.startsWith(`${name}.`)) return `{{${space}${names.get(name)}${body.slice(name.length)}}}`;
      }
      return token;
    });
  }
  return mapChildren(value, (child) => rewriteTemplates(child, names));
}

export function rewriteCondition(value: unknown, names: ReadonlyMap<string, string>): unknown {
  if (!isRecord(value)) return value;
  if (value.kind === 'literal') return value;
  if (value.kind === 'variable') {
    if (value.source !== undefined && value.source !== 'step') return value;
    const mapped = typeof value.stepId === 'string' ? names.get(value.stepId) : undefined;
    return mapped ? { ...value, stepId: mapped } : value;
  }
  return mapChildren(value, (child) => Array.isArray(child)
    ? child.map((item) => rewriteCondition(item, names)) : rewriteCondition(child, names));
}

export function remapConditionStep(condition: string | undefined, names: ReadonlyMap<string, string>): string {
  if (!condition) return '';
  const dot = condition.lastIndexOf('.');
  const mapped = dot > 0 ? names.get(condition.slice(0, dot)) : undefined;
  return mapped ? `${mapped}${condition.slice(dot)}` : condition;
}

export function rewriteNodeReferences(data: Record<string, unknown>, names: ReadonlyMap<string, string>): Record<string, unknown> {
  const next = { ...data };
  for (const field of ['config', 'targetMachineId', 'credentialId', 'breakpointCondition']) {
    if (field in data) next[field] = rewriteTemplates(data[field], names);
  }
  return next;
}

export function rewriteEdgeReferences(data: Record<string, unknown>, names: ReadonlyMap<string, string>): Record<string, unknown> {
  const condition = typeof data.condition === 'string' ? remapConditionStep(data.condition, names) : data.condition;
  const expression = rewriteCondition(data.conditionExpression, names);
  if (condition === data.condition && expression === data.conditionExpression) return data;
  return { ...data, ...(typeof condition === 'string' ? { condition } : {}),
    ...(expression !== undefined ? { conditionExpression: expression } : {}) };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function mapChildren(value: unknown, visit: (child: unknown) => unknown): unknown {
  if (Array.isArray(value)) {
    const next = value.map(visit);
    return next.every((child, i) => child === value[i]) ? value : next;
  }
  if (!isRecord(value)) return value;
  const next = Object.fromEntries(Object.entries(value).map(([key, child]) => [key, visit(child)]));
  return Object.keys(value).every((key) => value[key] === next[key]) ? value : next;
}
