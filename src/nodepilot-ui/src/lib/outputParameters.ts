/**
 * Parse helper for `StepExecution.OutputParametersJson` / `StepUpdate.outputParameters`.
 *
 * The backend stores the `param.*` variables captured by the PowerShell wrapper as a compact
 * JSON object of string to string, already redacted server-side. Every value is stringified so
 * non-string outputs such as `number` or `boolean` do not surprise a consumer. Returns `null`
 * for empty, invalid or non-object JSON instead of throwing; callers read that as "no params
 * available" and decide whether to fall back to another source, such as scanning stdout.
 */
export function parseOutputParametersJson(
  json?: string | null,
): Record<string, string> | null {
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return Object.fromEntries(
        Object.entries(parsed as Record<string, unknown>).map(([k, v]) => [k, String(v ?? '')]),
      );
    }
  } catch {
    // Best-effort: malformed JSON yields no params, and the caller may fall back elsewhere.
  }
  return null;
}