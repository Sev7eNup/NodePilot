import type { Edge, Node } from '@xyflow/react';
import i18n from '../i18n';

export interface CollapsedGraphView {
  nodes: Node[];
  edges: Edge[];
}

const COLLAPSED_GROUP_SIZE = { width: 240, height: 74 };

export function buildCollapsedGraphView(nodes: Node[], edges: Edge[]): CollapsedGraphView {
  const collapsedGroupIds = new Set(
    nodes
      .filter((n) => n.type === 'group' && (n.data as Record<string, unknown>)?.collapsed === true)
      .map((n) => n.id),
  );
  if (collapsedGroupIds.size === 0) return { nodes, edges };

  const hiddenToGroup = new Map<string, string>();
  for (const node of nodes) {
    if (node.parentId && collapsedGroupIds.has(node.parentId)) {
      hiddenToGroup.set(node.id, node.parentId);
    }
  }

  const childCounts = new Map<string, number>();
  for (const groupId of hiddenToGroup.values()) {
    childCounts.set(groupId, (childCounts.get(groupId) ?? 0) + 1);
  }

  const visibleNodes = nodes
    .filter((n) => !hiddenToGroup.has(n.id))
    .map((n) => {
      if (!collapsedGroupIds.has(n.id)) return n;
      const data = (n.data as Record<string, unknown>) ?? {};
      const width = typeof n.style?.width === 'number' ? n.style.width : COLLAPSED_GROUP_SIZE.width;
      const height = typeof n.style?.height === 'number' ? n.style.height : COLLAPSED_GROUP_SIZE.height;
      return {
        ...n,
        style: { ...n.style, width: COLLAPSED_GROUP_SIZE.width, height: COLLAPSED_GROUP_SIZE.height },
        data: {
          ...data,
          __collapsedChildCount: childCounts.get(n.id) ?? 0,
          __expandedWidth: width,
          __expandedHeight: height,
        },
      };
    });

  const summary = new Map<string, { edge: Edge; count: number }>();
  const visibleEdges: Edge[] = [];

  for (const edge of edges) {
    const sourceGroup = hiddenToGroup.get(edge.source);
    const targetGroup = hiddenToGroup.get(edge.target);
    const source = sourceGroup ?? edge.source;
    const target = targetGroup ?? edge.target;

    if (sourceGroup && targetGroup && sourceGroup === targetGroup) continue;
    if (!sourceGroup && !targetGroup) {
      visibleEdges.push(edge);
      continue;
    }

    const key = `${source}->${target}:${edge.sourceHandle ?? ''}:${edge.targetHandle ?? ''}`;
    const existing = summary.get(key);
    if (existing) {
      existing.count += 1;
      existing.edge.data = { ...((existing.edge.data as Record<string, unknown>) ?? {}), label: i18n.t('designer:collapsed.hiddenEdges', { count: existing.count }) };
      continue;
    }

    summary.set(key, {
      count: 1,
      edge: {
        ...edge,
        id: `collapsed-${key}`,
        source,
        target,
        selectable: false,
        deletable: false,
        reconnectable: false,
        data: {
          ...((edge.data as Record<string, unknown>) ?? {}),
          label: i18n.t('designer:collapsed.hiddenEdge'),
          __collapsedSummary: true,
        },
      },
    });
  }

  return { nodes: visibleNodes, edges: [...visibleEdges, ...[...summary.values()].map((s) => s.edge)] };
}

export function getExpandedGroupSize(node: Node): { width: number; height: number } {
  const data = (node.data as Record<string, unknown>) ?? {};
  const saved = data.expandedSize as { width?: unknown; height?: unknown } | undefined;
  const fallbackWidth = typeof data.__expandedWidth === 'number'
    ? data.__expandedWidth
    : typeof node.style?.width === 'number'
      ? node.style.width
      : 360;
  const fallbackHeight = typeof data.__expandedHeight === 'number'
    ? data.__expandedHeight
    : typeof node.style?.height === 'number'
      ? node.style.height
      : 220;
  return {
    width: typeof saved?.width === 'number' ? saved.width : fallbackWidth,
    height: typeof saved?.height === 'number' ? saved.height : fallbackHeight,
  };
}

export function getCollapsedGroupSize(): { width: number; height: number } {
  return COLLAPSED_GROUP_SIZE;
}
