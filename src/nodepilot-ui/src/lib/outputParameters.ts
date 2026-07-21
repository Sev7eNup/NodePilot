/**
 * Parse helper for `StepExecution.OutputParametersJson` / `StepUpdate.outputParameters`.
 *
 * The backend persists the `param.*` variables captured by the PowerShell wrapper as a
 * compact JSON object in `StepExecution.OutputParametersJson` (string→string, already
 * redacted server-side). This helper parses that JSON defensively and stringifies every
 * value (`String(v ?? '')`) so non-string outputs (e.g. `number`/`boolean`) never surprise
 * a consumer. Returns `null` when the JSON is empty/invalid/not-an-object — consumers should
 * treat that as "no params available" and decide for themselves whether to fall back to
 * something else (e.g. scanning stdout) rather than the helper throwing.
 *
 * Some call sites (`PropertiesPanel`, `variablePreview`) additionally need to look up a
 * single param by name; they call this helper and then do their own
 * `hasOwnProperty`/`map[name]`. The reducer hydration (`buildDatabusFromHydratedSteps`)
 * needs the whole map instead.
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
    // Best-effort: malformed JSON → no params (caller may fall back to another source).
  }
  return null;
}