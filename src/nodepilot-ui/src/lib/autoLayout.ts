import dagre from '@dagrejs/dagre';
import type { Node, Edge } from '@xyflow/react';

const defaultW = 220;
const defaultH = 110;

function buildDagreGraph(
  nodes: Node[],
  edges: Edge[],
  opts: { rankdir: string; ranksep: number; nodesep: number },
) {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ ...opts, marginx: 40, marginy: 40 });
  for (const n of nodes) {
    g.setNode(n.id, { width: n.measured?.width ?? defaultW, height: n.measured?.height ?? defaultH });
  }
  for (const e of edges) {
    if ((e.data as Record<string, unknown>)?.disabled) continue;
    g.setEdge(e.source, e.target);
  }
  return g;
}

type DagreGraph = ReturnType<typeof buildDagreGraph>;

function mapDagreResult(nodes: Node[], g: DagreGraph): Node[] {
  return nodes.map((n) => {
    const laid = g.node(n.id) as { x: number; y: number } | undefined;
    if (!laid) return n;
    const w = n.measured?.width ?? defaultW;
    const h = n.measured?.height ?? defaultH;
    return { ...n, position: { x: Math.round(laid.x - w / 2), y: Math.round(laid.y - h / 2) } };
  });
}

/** LR — standard left-to-right (default, matches CLAUDE.md styleguide). */
export function autoLayout(nodes: Node[], edges: Edge[]): Node[] {
  const g = buildDagreGraph(nodes, edges, { rankdir: 'LR', ranksep: 180, nodesep: 80 });
  dagre.layout(g as Parameters<typeof dagre.layout>[0]);
  return mapDagreResult(nodes, g);
}

/** TB — top-to-bottom; useful for tree-shaped workflows. */
export function autoLayoutTB(nodes: Node[], edges: Edge[]): Node[] {
  const g = buildDagreGraph(nodes, edges, { rankdir: 'TB', ranksep: 120, nodesep: 60 });
  dagre.layout(g as Parameters<typeof dagre.layout>[0]);
  return mapDagreResult(nodes, g);
}

/** Compact — LR with tighter spacing for dense or large workflows. */
export function autoLayoutCompact(nodes: Node[], edges: Edge[]): Node[] {
  const g = buildDagreGraph(nodes, edges, { rankdir: 'LR', ranksep: 100, nodesep: 35 });
  dagre.layout(g as Parameters<typeof dagre.layout>[0]);
  return mapDagreResult(nodes, g);
}

/** ELK layered — minimises edge crossings better than dagre on complex fan-out graphs. Async. */
export async function autoLayoutELK(nodes: Node[], edges: Edge[]): Promise<Node[]> {
  // Dynamic import keeps ELK (~1 MB) out of the initial bundle.
  const ELKModule = await import('elkjs/lib/elk.bundled.js');
  const ELK = (ELKModule as unknown as { default: new () => { layout: (g: unknown) => Promise<{ children?: Array<{ id: string; x?: number; y?: number }> }> } }).default;
  const elk = new ELK();

  const graph = {
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'RIGHT',
      'elk.layered.spacing.nodeNodeBetweenLayers': '120',
      'elk.spacing.nodeNode': '40',
      'elk.padding': '[top=40, left=40, bottom=40, right=40]',
    },
    children: nodes.map((n) => ({
      id: n.id,
      width: n.measured?.width ?? defaultW,
      height: n.measured?.height ?? defaultH,
    })),
    edges: edges
      .filter((e) => !(e.data as Record<string, unknown>)?.disabled)
      .map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };

  const laid = await elk.layout(graph);
  const childMap = new Map((laid.children ?? []).map((c) => [c.id, c]));
  return nodes.map((n) => {
    const c = childMap.get(n.id);
    if (c?.x == null || c?.y == null) return n;
    return { ...n, position: { x: Math.round(c.x), y: Math.round(c.y) } };
  });
}
