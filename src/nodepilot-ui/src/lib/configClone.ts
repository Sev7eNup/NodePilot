import { REMOTE_ACTIVITY_TYPES } from './activityCatalog.generated';

/**
 * Generic clone keys: target machine, credential. Read directly off `node.data`.
 * Available for every Remote-Activity (REMOTE_ACTIVITY_TYPES). Cloning these alone
 * is the most common case — same target host, different action.
 */
export const SHARED_NODE_CLONE_KEYS = ['targetMachineId', 'credentialId'] as const;

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
 * complete source config.
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
  // paths, URLs, etc.). Users explicitly want a full copy: "this new step should look
  // exactly like that one, then I'll edit what I need."
  const sourceConfig = (source.config as Record<string, unknown> | undefined) ?? {};
  const configPatch: Record<string, unknown> = { ...sourceConfig };
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
