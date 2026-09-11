import type { Edge, Node } from '@xyflow/react';

/**
 * Render-only fields the canvas projection writes onto nodes and edges. They carry no
 * authored meaning, so they never belong in a saved definition. `__`-prefixed data keys are
 * covered by the prefix rule below; these are the ones without a prefix.
 */
const RUNTIME_DATA_KEYS = ['inDegreeCount'] as const;

function stripRuntimeData<T extends Record<string, unknown> | null | undefined>(data: T): T {
  if (!data || typeof data !== 'object') return data;

  let changed = false;
  const cleaned: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(data)) {
    if (key.startsWith('__') || (RUNTIME_DATA_KEYS as readonly string[]).includes(key)) {
      changed = true;
      continue;
    }
    cleaned[key] = value;
  }

  return changed ? cleaned as T : data;
}

/** Drops the given projection props from a node or edge, keeping the object identity when none were set. */
function stripRuntimeProps<T extends object>(element: T, props: readonly string[]): T {
  const present = props.filter((p) => p in element);
  if (present.length === 0) return element;
  const cleaned = { ...element } as Record<string, unknown>;
  for (const p of present) delete cleaned[p];
  return cleaned as T;
}

const NODE_RUNTIME_PROPS = ['hidden', 'className'] as const;
const EDGE_RUNTIME_PROPS = ['hidden', 'animated'] as const;

export function stripRuntimeDefinition(definition: { nodes: Node[]; edges: Edge[] }): { nodes: Node[]; edges: Edge[] } {
  return {
    nodes: definition.nodes.map((node) => {
      const data = stripRuntimeData(node.data as Record<string, unknown>);
      const stripped = stripRuntimeProps(node, NODE_RUNTIME_PROPS);
      if (data === node.data && stripped === node) return node;
      return { ...stripped, data };
    }),
    edges: definition.edges.map((edge) => {
      const data = stripRuntimeData(edge.data as Record<string, unknown> | undefined);
      const stripped = stripRuntimeProps(edge, EDGE_RUNTIME_PROPS);
      if (data === edge.data && stripped === edge) return edge;
      return { ...stripped, data };
    }),
  };
}
