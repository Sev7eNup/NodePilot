export type JsonPathSegment = string | number;

export function tryParseJson(value: string | null | undefined): unknown | null {
  if (!value?.trim()) return null;
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return null;
  }
}

export function buildJsonPath(segments: JsonPathSegment[]): string {
  return segments.reduce<string>((path, segment) => {
    if (typeof segment === 'number') return `${path}[${segment}]`;
    return isSimpleProperty(segment)
      ? `${path}.${segment}`
      : `${path}['${segment.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}']`;
  }, '$');
}

export function describeJsonValue(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return `array(${value.length})`;
  if (typeof value === 'object') return `object(${Object.keys(value as Record<string, unknown>).length})`;
  if (typeof value === 'string') return value.length > 42 ? `${value.slice(0, 42)}...` : value;
  return String(value);
}

function isSimpleProperty(value: string): boolean {
  return /^[A-Za-z_$][\w$]*$/.test(value);
}
