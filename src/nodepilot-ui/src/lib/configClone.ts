import { REMOTE_ACTIVITY_TYPES } from './activityCatalog.generated';

/**
 * Node-data keys that can be cloned between any two remote activities
 * (REMOTE_ACTIVITY_TYPES): target machine and credential. Read directly off `node.data`.
 */
export const SHARED_NODE_CLONE_KEYS = ['targetMachineId', 'credentialId'] as const;

/**
 * Returns true if `targetMachineId` and `credentialId` are meaningful for this activity.
 * Decides whether the clone picker offers a cross-type clone between two remote activities,
 * such as copying the target machine from a runScript onto a serviceManagement step.
 */
export function isRemoteActivityType(activityType: string): boolean {
  return REMOTE_ACTIVITY_TYPES.has(activityType);
}

export type CloneScope = 'all' | 'remoteOnly';

/**
 * Builds a delta of node-data fields to overwrite on the target. The caller merges the result
 * onto the current node data, usually through `onUpdate` in PropertiesPanel.
 *
 * `scope = 'remoteOnly'` is the cross-type case and copies only `targetMachineId` and
 * `credentialId`, so several remote steps can point at one host without sharing timeout or
 * retry settings. `scope = 'all'` requires identical activity types and copies the shared keys
 * plus the complete source config.
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

  // scope === 'all': same-type clone.
  if (sourceActivityType !== targetActivityType) return patch;

  if (isRemoteActivityType(sourceActivityType)) {
    for (const k of SHARED_NODE_CLONE_KEYS) {
      if (k in source) patch[k] = source[k] ?? null;
    }
  }

  // Take the entire source config, including the action payload (script bodies, queries,
  // paths, URLs), because a clone is meant to be a full copy the user then edits.
  const sourceConfig = (source.config as Record<string, unknown> | undefined) ?? {};
  const configPatch: Record<string, unknown> = { ...sourceConfig };
  if (Object.keys(configPatch).length > 0) {
    patch.__configPatch = configPatch;
  }
  return patch;
}

/**
 * Applies a patch produced by `buildClonedDataPatch` onto a target node-data object and
 * returns a new object. The `__configPatch` marker is unwrapped here so callers can pass the
 * result straight to `onUpdate(nodeId, data)`.
 *
 * The patched config replaces the target config instead of merging into it. A merge would keep
 * target keys the source does not have, so the clone would not match the source.
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
