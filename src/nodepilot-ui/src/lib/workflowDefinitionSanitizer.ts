import type { Edge, Node } from '@xyflow/react';

function stripRuntimeData<T extends Record<string, unknown> | null | undefined>(data: T): T {
  if (!data || typeof data !== 'object') return data;

  let changed = false;
  const cleaned: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(data)) {
    if (key.startsWith('__')) {
      changed = true;
      continue;
    }
    cleaned[key] = value;
  }

  return changed ? cleaned as T : data;
}

export function stripRuntimeDefinition(definition: { nodes: Node[]; edges: Edge[] }): { nodes: Node[]; edges: Edge[] } {
  return {
    nodes: definition.nodes.map((node) => {
      const data = stripRuntimeData(node.data as Record<string, unknown>);
      return data === node.data ? node : { ...node, data };
    }),
    edges: definition.edges.map((edge) => {
      const data = stripRuntimeData(edge.data as Record<string, unknown> | undefined);
      return data === edge.data ? edge : { ...edge, data };
    }),
  };
}
