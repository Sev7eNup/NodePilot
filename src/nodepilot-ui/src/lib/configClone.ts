import { REMOTE_ACTIVITY_TYPES } from './activityCatalog.generated';

/**
 * Generic clone keys: target machine, credential. Read directly off `node.data`.
 * Available for every Remote-Activity (REMOTE_ACTIVITY_TYPES). Cloning these alone
 * is the most common case — same target host, different action.
 */
export const SHARED_NODE_CLONE_KEYS = ['targetMachineId', 'credentialId'] as const;

/**
 * Per-activity-type list of `data.config.*` keys to skip when cloning. The default rule is
 * "copy the entire config" — when the user picks a source step they want the whole step,
 * not a half-copy that forces them to re-enter the script body. Skip-list exists only for
 * fields that would always be wrong on the target (e.g. an inherited execution-time `result`
 * stamped into the source's config from a previous run).
 *
 * Default = empty skip list, so adding a new activity type needs no entry here.
 */
const CONFIG_CLONE_SKIP_KEYS_BY_TYPE: Record<string, ReadonlyArray<string>> = {
  // No type-specific skips today — defaults handled by SHARED_CONFIG_SKIP_KEYS below.
};

/** Keys that never make sense to copy regardless of activity type — runtime-stamped state. */
const SHARED_CONFIG_SKIP_KEYS: ReadonlyArray<string> = [];

/**
 * Returns the list of `config.*` keys that are explicitly skipped for an activity type. Used
 * by tests + the popover preview text. Empty array → "everything in config is cloneable".
 */
export function skippedConfigKeys(activityType: string): ReadonlyArray<string> {
  const typeSpecific = CONFIG_CLONE_SKIP_KEYS_BY_TYPE[activityType] ?? [];
  return [...SHARED_CONFIG_SKIP_KEYS, ...typeSpecific];
}

/**
 * Back-compat alias for the older "include-list" API. Now returns an empty array when no
 * skips are configured; callers should treat "no skips" as "every key is cloneable" rather
 * than "nothing is cloneable" (the inverse semantics from before the cloning rules were
 * relaxed to copy entire configs).
 *
 * @deprecated Read `skippedConfigKeys` directly — the include-list shape no longer maps onto
 * the actual cloning behaviour.
 */
export function cloneableConfigKeys(activityType: string): ReadonlyArray<string> {
  return skippedConfigKeys(activityType);
}

/**
 * Returns true if `targetMachineId` + `credentialId` are meaningful for this activity.
 * Used to decide whether the clone-picker should offer cross-type Remote-→-Remote
 * (e.g. clone target machine from a runScript onto a serviceManagement step).
 */
export function isRemoteActivityType(activityType: string): boolean {
  return REMOTE_ACTIVITY_TYPES.has(activityType);
}

export type CloneScope = 'all' | 'remoteOnly';

/**
 * Builds a delta of node-data fields to overwrite on the target. Caller merges the result
 * onto current node-data (typically via the existing `onUpdate` plumbing in PropertiesPanel).
 *
 * `scope = 'remoteOnly'` is the cross-type case: only `targetMachineId` + `credentialId` are
 * copied. Useful when you want "every remote step on this graph hits the same host" without
 * dragging timeout/retry policy along.
 *
 * `scope = 'all'` requires identical activity types — copies the shared keys plus the
 * type-specific cloneable config keys.
 */
export function buildClonedDataPatch(
  source: Record<string, unknown>,
  targetActivityType: string,
  scope: CloneScope,
): Record<string, unknown> {
  const patch: Record<string, unknown> = {};
  const sourceActivityType = (source.activityType as string) || '';

  if (scope === 'remoteOnly') {
    // Only meaningful when both ends are remote-capable.
    if (!isRemoteActivityType(sourceActivityType) || !isRemoteActivityType(targetActivityType)) {
      return patch;
    }
    for (const k of SHARED_NODE_CLONE_KEYS) {
      if (k in source) patch[k] = source[k] ?? null;
    }
    return patch;
  }

  // scope === 'all' — same-type clone.
  if (sourceActivityType !== targetActivityType) return patch;

  if (isRemoteActivityType(sourceActivityType)) {
    for (const k of SHARED_NODE_CLONE_KEYS) {
      if (k in source) patch[k] = source[k] ?? null;
    }
  }

  // Take the entire source config (including the action payload — script bodies, queries,
  // paths, URLs, etc.) and only drop runtime-stamped keys. Users explicitly want a full
  // copy: "this new step should look exactly like that one, then I'll edit what I need."
  const sourceConfig = (source.config as Record<string, unknown> | undefined) ?? {};
  const skip = new Set(skippedConfigKeys(targetActivityType));
  const configPatch: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(sourceConfig)) {
    if (skip.has(k)) continue;
    configPatch[k] = v;
  }
  if (Object.keys(configPatch).length > 0) {
    patch.__configPatch = configPatch;
  }
  return patch;
}

/**
 * Applies a patch produced by `buildClonedDataPatch` onto a target node-data object,
 * returning a new object. The `__configPatch` marker is unwrapped here so callers can
 * just pass the result to their `onUpdate(nodeId, data)` plumbing.
 *
 * Config replacement strategy: the patched config REPLACES the target's config rather than
 * merging. Otherwise, when cloning a runScript onto a step that already had `script: 'foo'`
 * but the source had no `script` at all, the old `foo` would survive — defeating the user's
 * intent. The clone is "make this step look like that one"; merging is the wrong default.
 */
export function applyClonedPatch(
  targetData: Record<string, unknown>,
  patch: Record<string, unknown>,
): Record<string, unknown> {
  const next: Record<string, unknown> = { ...targetData };
  for (const [k, v] of Object.entries(patch)) {
    if (k === '__configPatch') {
      next.config = { ...(v as Record<string, unknown>) };
    } else {
      next[k] = v;
    }
  }
  return next;
}
