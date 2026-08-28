import type { Connection, Edge, Node } from '@xyflow/react';
import {
  DEFAULT_SOURCE_PORT,
  DEFAULT_TARGET_PORT,
  normalizePort,
  oppositePort,
} from './edgePorts';

export interface JunctionFanInIds {
  junctionId: string;
  incomingEdgeId: string;
  outgoingEdgeId: string;
  label: string;
  movingEdge?: Edge;
}

function activityTypeOf(node: Node | undefined): string | undefined {
  const fromData = (node?.data as Record<string, unknown> | undefined)?.activityType;
  if (typeof fromData === 'string') return fromData;
  return node?.type && node.type !== 'activity' ? node.type : undefined;
}

export function requiresJunctionForConnection(
  targetId: string | null | undefined,
  nodes: Node[],
  edges: Edge[],
  excludedEdgeId?: string,
): boolean {
  if (!targetId) return false;
  if (activityTypeOf(nodes.find((node) => node.id === targetId))?.toLowerCase() === 'junction') return false;
  return edges.some((edge) => edge.id !== excludedEdgeId && edge.target === targetId);
}

export function insertJunctionForFanIn(
  nodes: Node[],
  edges: Edge[],
  connection: Connection,
  ids: JunctionFanInIds,
): { nodes: Node[]; edges: Edge[]; junctionId: string } {
  if (!connection.source || !connection.target)
    throw new Error('A fan-in connection requires source and target nodes.');

  const targetNode = nodes.find((node) => node.id === connection.target);
  if (!targetNode) throw new Error(`Target node '${connection.target}' was not found.`);

  const baseEdges = ids.movingEdge
    ? edges.filter((edge) => edge.id !== ids.movingEdge!.id)
    : edges;
  const existingIncoming = baseEdges.filter((edge) => edge.target === connection.target);
  const targetPort = normalizePort(
    connection.targetHandle ?? existingIncoming[0]?.targetHandle,
    DEFAULT_TARGET_PORT,
  );
  const newSourcePort = normalizePort(connection.sourceHandle, DEFAULT_SOURCE_PORT);

  const junction: Node = {
    id: ids.junctionId,
    type: 'activity',
    position: {
      x: Math.round(targetNode.position.x - 220),
      y: Math.round(targetNode.position.y),
    },
    data: {
      label: ids.label,
      activityType: 'junction',
      targetMachineId: null,
      credentialId: null,
      config: { mode: 'waitAll' },
    },
  };

  const rewired = baseEdges.map((edge) => edge.target === connection.target
    ? {
        ...edge,
        target: ids.junctionId,
        targetHandle: oppositePort(normalizePort(edge.sourceHandle, DEFAULT_SOURCE_PORT)),
      }
    : edge);

  const incomingEdge: Edge = ids.movingEdge
    ? {
        ...ids.movingEdge,
        source: connection.source,
        target: ids.junctionId,
        sourceHandle: newSourcePort,
        targetHandle: oppositePort(newSourcePort),
      }
    : {
        id: ids.incomingEdgeId,
        source: connection.source,
        target: ids.junctionId,
        sourceHandle: newSourcePort,
        targetHandle: oppositePort(newSourcePort),
        type: 'labeled',
        data: { label: '', condition: '', disabled: false },
      };
  const outgoingEdge: Edge = {
    id: ids.outgoingEdgeId,
    source: ids.junctionId,
    target: connection.target,
    sourceHandle: oppositePort(targetPort),
    targetHandle: targetPort,
    type: 'labeled',
    data: { label: '', condition: '', disabled: false },
  };

  return {
    nodes: [...nodes, junction],
    edges: [...rewired, incomingEdge, outgoingEdge],
    junctionId: ids.junctionId,
  };
}
