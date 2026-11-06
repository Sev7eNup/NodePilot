import type { Node, Edge } from '@xyflow/react';

export type MatchKind = 'node-label' | 'edge-label' | 'config';

export interface MatchLocation {
  kind: MatchKind;
  nodeId?: string;
  edgeId?: string;
  /** Dot-path into config for display, e.g. "script" or "headers.0.value" */
  configKeyPath?: string;
  /** The same path as segments; replacement uses it, because a key may itself contain a dot. */
  configPath?: string[];
  /** The matched substring */
  matchedText: string;
  /** Short context around the match for display */
  contextSnippet: string;
  /** Human-readable display name */
  displayName: string;
}

export interface FindReplaceScopes {
  nodeLabels: boolean;
  edgeLabels: boolean;
  configValues: boolean;
}

function snippet(text: string, query: string): string {
  const lower = text.toLowerCase();
  const idx = lower.indexOf(query.toLowerCase());
  if (idx < 0) return text.slice(0, 60);
  const start = Math.max(0, idx - 20);
  const end = Math.min(text.length, idx + query.length + 20);
  return (start > 0 ? '…' : '') + text.slice(start, end) + (end < text.length ? '…' : '');
}

function traverseObject(
  obj: unknown,
  query: string,
  pathParts: string[],
  results: Array<{ path: string[]; text: string; context: string }>,
): void {
  if (typeof obj === 'string') {
    if (obj.toLowerCase().includes(query.toLowerCase())) {
      results.push({ path: pathParts, text: obj, context: snippet(obj, query) });
    }
    return;
  }
  if (Array.isArray(obj)) {
    obj.forEach((item, i) => traverseObject(item, query, [...pathParts, String(i)], results));
    return;
  }
  if (obj && typeof obj === 'object') {
    for (const [key, val] of Object.entries(obj as Record<string, unknown>)) {
      traverseObject(val, query, [...pathParts, key], results);
    }
  }
}

/** How many matches the overlay lists. Only the list is capped; Replace All covers every match. */
export const MAX_DISPLAYED_MATCHES = 50;

export function findMatches(
  query: string,
  nodes: Node[],
  edges: Edge[],
  scopes: FindReplaceScopes,
): MatchLocation[] {
  if (!query) return [];
  const results: MatchLocation[] = [];
  const q = query.toLowerCase();

  if (scopes.nodeLabels) {
    for (const n of nodes) {
      const label = (n.data as Record<string, unknown>).label as string | undefined;
      if (label?.toLowerCase().includes(q)) {
        results.push({
          kind: 'node-label',
          nodeId: n.id,
          matchedText: label,
          contextSnippet: snippet(label, query),
          displayName: label || n.id,
        });
      }
    }
  }

  if (scopes.edgeLabels) {
    for (const e of edges) {
      const label = (e.data as Record<string, unknown>)?.label as string | undefined;
      if (label?.toLowerCase().includes(q)) {
        results.push({
          kind: 'edge-label',
          edgeId: e.id,
          matchedText: label,
          contextSnippet: snippet(label, query),
          displayName: `Edge → ${label}`,
        });
      }
    }
  }

  if (scopes.configValues) {
    for (const n of nodes) {
      const config = (n.data as Record<string, unknown>).config;
      if (!config) continue;
      const hits: Array<{ path: string[]; text: string; context: string }> = [];
      traverseObject(config, query, [], hits);
      const nodeLabel = (n.data as Record<string, unknown>).label as string | undefined;
      for (const h of hits) {
        const keyPath = h.path.join('.');
        results.push({
          kind: 'config',
          nodeId: n.id,
          configKeyPath: keyPath,
          configPath: h.path,
          matchedText: h.text,
          contextSnippet: h.context,
          displayName: `${nodeLabel || n.id} › ${keyPath || 'config'}`,
        });
      }
    }
  }

  return results;
}

/** Replaces inside the one string at `path`; every other config value is left untouched. */
function replaceAtPath(value: unknown, path: string[], search: string, replacement: string): unknown {
  if (path.length === 0) return typeof value === 'string' ? value.replaceAll(search, replacement) : value;
  const [head, ...rest] = path;
  if (Array.isArray(value)) {
    const index = Number(head);
    if (!Number.isInteger(index) || index < 0 || index >= value.length) return value;
    return value.map((item, i) => (i === index ? replaceAtPath(item, rest, search, replacement) : item));
  }
  if (value && typeof value === 'object' && Object.hasOwn(value, head)) {
    const record = value as Record<string, unknown>;
    return { ...record, [head]: replaceAtPath(record[head], rest, search, replacement) };
  }
  return value;
}

export function applyReplaceAll(
  matches: MatchLocation[],
  search: string,
  replacement: string,
  nodes: Node[],
  edges: Edge[],
): { nodes: Node[]; edges: Edge[] } {
  const nodeIds = new Set(matches.filter((m) => m.nodeId).map((m) => m.nodeId!));
  const edgeIds = new Set(matches.filter((m) => m.edgeId).map((m) => m.edgeId!));

  const newNodes = nodes.map((n) => {
    if (!nodeIds.has(n.id)) return n;
    const hasLabelMatch = matches.some((m) => m.nodeId === n.id && m.kind === 'node-label');
    let data = n.data as Record<string, unknown>;
    if (hasLabelMatch && typeof data.label === 'string') {
      data = { ...data, label: data.label.replaceAll(search, replacement) };
    }
    for (const m of matches) {
      if (m.nodeId !== n.id || m.kind !== 'config' || !m.configPath) continue;
      data = { ...data, config: replaceAtPath(data.config, m.configPath, search, replacement) };
    }
    return { ...n, data };
  });

  const newEdges = edges.map((e) => {
    if (!edgeIds.has(e.id)) return e;
    const label = (e.data as Record<string, unknown>)?.label as string | undefined;
    if (!label) return e;
    return { ...e, data: { ...(e.data as Record<string, unknown>), label: label.replaceAll(search, replacement) } };
  });

  return { nodes: newNodes, edges: newEdges };
}
