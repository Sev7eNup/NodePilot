import type { Node } from '@xyflow/react';
import {
  extractBaseUrl,
  getActivitySmartDefaults,
  type SmartDefaults,
} from './activityConfigFacts';

export { extractBaseUrl };
export type { SmartDefaults };

/**
 * Picks the "most recent" sibling of the given activity type. "Recent" is defined by the
 * rightmost x-position, tie-broken by the largest y-position, then by array index.
 */
export function findLastSimilarNode(nodes: Node[], activityType: string): Node | undefined {
  let best: Node | undefined;
  let bestIndex = -1;
  for (let i = 0; i < nodes.length; i++) {
    const node = nodes[i];
    const type = (node.data as Record<string, unknown> | undefined)?.activityType;
    if (type !== activityType) continue;
    if (!best) { best = node; bestIndex = i; continue; }
    const bestPosition = best.position;
    const currentPosition = node.position;
    if (currentPosition.x > bestPosition.x) { best = node; bestIndex = i; continue; }
    if (currentPosition.x < bestPosition.x) continue;
    if (currentPosition.y > bestPosition.y) { best = node; bestIndex = i; continue; }
    if (currentPosition.y < bestPosition.y) continue;
    if (i > bestIndex) { best = node; bestIndex = i; }
  }
  return best;
}

/**
 * Returns a partial NodeData payload that should be merged into the freshly-created node.
 * Activity-specific facts live in activityConfigFacts; this module only chooses the latest
 * sibling node that supplies those facts.
 */
export function getSmartDefaults(activityType: string, nodes: Node[]): SmartDefaults {
  const last = findLastSimilarNode(nodes, activityType);
  if (!last) return {};
  const lastData = (last.data as Record<string, unknown>) ?? {};
  const lastConfig = (lastData.config as Record<string, unknown> | undefined) ?? {};
  return getActivitySmartDefaults(activityType, lastData, lastConfig);
}
