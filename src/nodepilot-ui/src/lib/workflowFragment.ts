import type { Edge, Node } from '@xyflow/react';
import { randomUuid } from './uuid';
import { rewriteNodeReferences, rewriteEdgeReferences } from './workflowReferences';
export { remapConditionStep } from './workflowReferences';
import { stripRuntimeDefinition } from './workflowDefinitionSanitizer';

/**
 * Part of a workflow: some nodes and the edges between them, in the same shape as a saved
 * workflow definition. Copy and paste in the designer move it through the system clipboard as
 * JSON text; duplicating moves it directly.
 */
export interface WorkflowFragment {
  nodes: Node[];
  edges: Edge[];
}

/** Canvas state React Flow keeps on nodes and edges. It means nothing outside the editor. */
const VIEW_PROPS = ['selected', 'dragging', 'measured', 'resizing'] as const;
const NODE_TYPES: ReadonlySet<string> = new Set(['activity', 'group', 'stickyNote']);
/** The mask the server writes in place of a secret. */
const REDACTED = '***';

/**
 * Config keys whose values are secrets, lower-cased. Mirror of
 * `WorkflowSecretKeys.SecretConfigKeys` (checked by `workflowFragment.test.ts`).
 */
export const SECRET_CONFIG_KEYS: ReadonlySet<string> = new Set([
  'secret', 'apikey', 'password', 'authtoken', 'bearer', 'connectionstring',
  'token', 'accesstoken', 'refreshtoken', 'sessiontoken', 'clientsecret', 'privatekey',
  'accesskey', 'secretkey', 'apisecret', 'webhooksecret',
  'authorization', 'proxy-authorization', 'cookie', 'set-cookie',
  'x-api-key', 'x-auth-token', 'x-webhook-secret',
]);

/**
 * The given nodes, the nodes inside any given group, and every edge whose two ends are among
 * them. Node and edge data are copied, so later edits on the canvas do not reach into the
 * fragment.
 */
export function selectFragment(nodes: Node[], edges: Edge[], nodeIds: Iterable<string>): WorkflowFragment {
  const ids = new Set(nodeIds);
  const groupIds = new Set(nodes.filter((n) => n.type === 'group' && ids.has(n.id)).map((n) => n.id));
  for (const n of nodes) if (n.parentId && groupIds.has(n.parentId)) ids.add(n.id);
  return {
    nodes: nodes
      .filter((n) => ids.has(n.id))
      .map((n) => ({ ...n, data: { ...(n.data as object) } as Node['data'] })),
    edges: edges
      .filter((e) => ids.has(e.source) && ids.has(e.target))
      .map((e) => ({ ...e, data: e.data ? { ...(e.data as object) } : undefined })),
  };
}

/**
 * The fragment as indented JSON text: the definition shape, without canvas-only state. Values of
 * secret config keys are written as `***`, as in a workflow export, because the text leaves the
 * app through the system clipboard.
 */
export function serializeFragment(fragment: WorkflowFragment): string {
  const clean = stripRuntimeDefinition(fragment);
  return JSON.stringify({
    nodes: clean.nodes.map(withoutViewProps).map((n) => ({ ...n, data: maskSecrets(n.data) as Node['data'] })),
    edges: clean.edges.map(withoutViewProps).map((e) => ({ ...e, ...(e.data ? { data: maskSecrets(e.data) } : {}) })),
  }, null, 2);
}

export type FragmentParseError = 'invalidJson' | 'noNodes' | 'invalidNode' | 'duplicateId';

export type FragmentParseResult =
  | {
    ok: true;
    fragment: WorkflowFragment;
    /** Edges left out because one of their ends is not part of the text. */
    droppedEdges: number;
    /** Values that are exactly the server's secret mask; pasted as they are, they stay `***`. */
    redactedValues: number;
  }
  | { ok: false; error: FragmentParseError };

/**
 * Reads pasted text as a fragment. Accepts any object with a `nodes` array, so a complete
 * workflow definition works as well. The text comes from outside the editor, so every node is
 * checked for the fields the canvas needs; edges that do not fit are left out.
 */
export function parseFragment(text: string): FragmentParseResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { ok: false, error: 'invalidJson' };
  }
  if (!isRecord(parsed) || !Array.isArray(parsed.nodes) || parsed.nodes.length === 0) {
    return { ok: false, error: 'noNodes' };
  }
  const nodes: Node[] = [];
  const ids = new Set<string>();
  for (const candidate of parsed.nodes) {
    if (!isNodeShape(candidate)) return { ok: false, error: 'invalidNode' };
    if (ids.has(candidate.id)) return { ok: false, error: 'duplicateId' };
    ids.add(candidate.id);
    nodes.push(candidate);
  }
  const rawEdges: unknown[] = Array.isArray(parsed.edges) ? parsed.edges : [];
  const edges = rawEdges
    .filter(isEdgeShape)
    .filter((e) => ids.has(e.source) && ids.has(e.target));
  const fragment = stripRuntimeDefinition({
    nodes: nodes.map(withoutViewProps),
    edges: edges.map(withoutViewProps),
  });
  return {
    ok: true,
    fragment,
    droppedEdges: rawEdges.length - edges.length,
    redactedValues: countRedacted([...fragment.nodes.map((n) => n.data), ...fragment.edges.map((e) => e.data)]),
  };
}

/**
 * Prepares a fragment for insertion: fresh IDs for every node and edge, and every reference
 * to an old node ID rewritten to the new one (edge ends, group membership, edge conditions,
 * `{{id.…}}` templates in node config). Top-level nodes move by (dx, dy); children keep their
 * position inside their pasted group. Only a non-group node can sit in a group, as in the
 * designer; a child whose group is neither pasted nor in the workflow becomes a top-level node.
 * The result is selected.
 */
export function remapFragment(
  fragment: WorkflowFragment,
  { existingGroupIds, existingAliases = new Set<string>(), dx, dy }: {
    existingGroupIds: ReadonlySet<string>; existingAliases?: ReadonlySet<string>; dx: number; dy: number;
  },
): WorkflowFragment {
  const idMap = new Map<string, string>();
  for (const n of fragment.nodes) idMap.set(n.id, `${idPrefix(n.type)}-${randomUuid().slice(0, 8)}`);
  const references = new Map(idMap);
  const aliases = new Set(existingAliases);
  for (const n of fragment.nodes) if (typeof n.data.outputVariable === 'string') aliases.add(n.data.outputVariable);
  for (const n of fragment.nodes) {
    const alias = n.data.outputVariable;
    if (typeof alias !== 'string' || !alias || references.has(alias)) continue;
    let suffix = 2;
    while (aliases.has(`${alias}_${suffix}`)) suffix++;
    const fresh = `${alias}_${suffix}`;
    aliases.add(fresh);
    references.set(alias, fresh);
  }
  const pastedGroupIds = new Set(fragment.nodes.filter((n) => n.type === 'group').map((n) => n.id));

  // React Flow needs a group before its children.
  const ordered = [
    ...fragment.nodes.filter((n) => n.type === 'group'),
    ...fragment.nodes.filter((n) => n.type !== 'group'),
  ];
  const nodes = ordered.map((n): Node => {
    const { parentId: oldParentId, extent, ...rest } = n;
    const parentRef = n.type === 'group' ? undefined : oldParentId;
    const pastedParent = parentRef && pastedGroupIds.has(parentRef) ? idMap.get(parentRef) : undefined;
    const parentId = pastedParent ?? (parentRef && existingGroupIds.has(parentRef) ? parentRef : undefined);
    const data = rewriteNodeReferences({ ...n.data, __liveStatus: undefined }, references);
    if (typeof data.outputVariable === 'string' && data.outputVariable) {
      data.outputVariable = references.get(data.outputVariable) ?? data.outputVariable;
    }
    return {
      ...rest,
      ...(parentId ? { parentId, ...(extent ? { extent } : {}) } : {}),
      id: idMap.get(n.id) ?? n.id,
      position: pastedParent ? n.position : { x: n.position.x + dx, y: n.position.y + dy },
      selected: true,
      data,
    };
  });

  const edges = fragment.edges.flatMap((e): Edge[] => {
    const source = idMap.get(e.source);
    const target = idMap.get(e.target);
    if (!source || !target) return [];
    return [{
      ...e,
      id: `edge-${randomUuid().slice(0, 8)}`,
      source,
      target,
      selected: true,
      data: e.data ? rewriteEdgeReferences(e.data, references) : e.data,
    }];
  });

  return { nodes, edges };
}

function idPrefix(type: string | undefined): string {
  if (type === 'group') return 'group';
  if (type === 'stickyNote') return 'note';
  return 'step';
}

function withoutViewProps<T extends object>(element: T): T {
  const copy = { ...element } as Record<string, unknown>;
  for (const prop of VIEW_PROPS) delete copy[prop];
  return copy as T;
}

const PUBLIC_HTTP_HEADERS = new Set(['accept', 'content-type', 'user-agent']);
const CREDENTIAL_HEADERS = new Set([
  'authorization', 'proxy-authorization', 'cookie', 'set-cookie',
  'x-api-key', 'x-auth-token', 'x-webhook-secret',
]);
const HEADER_LINE = /^\s*([!#$%&'*+.^_`|~A-Za-z0-9-]+)\s*:\s*([^\n]+)$/;
const SCHEME_PREFIX = /^(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)\s+/i;
const TOKEN_SHAPES = [
  /sk_(?:live|test)_[A-Za-z0-9]{16,}/,
  /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/,
  /\bgh[pousr]_[A-Za-z0-9]{20,}\b/,
  /\bglpat-[A-Za-z0-9_-]{20,}\b/,
  /\bxox[baprs]-[A-Za-z0-9-]{10,}\b/,
  /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/,
  /-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----/,
];
const QUOTED_SECRET = /(?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth[_-]?token|refresh[_-]?token|session[_-]?key|webhook[_-]?secret)["']?\s*[=:]\s*["'][^"']{6,}["']/i;

function withoutTemplates(value: string): string {
  return value.replace(/\{\{[^}]*\}\}/g, ' ');
}

function isLiteralSecretHeader(name: string, value: string): boolean {
  return !PUBLIC_HTTP_HEADERS.has(name.toLowerCase())
    && withoutTemplates(value).replace(SCHEME_PREFIX, '').trim().length > 0;
}

/** Mirrors WorkflowSecretContent.LooksSecret, including references inside arbitrary fields. */
function looksSecret(value: string): boolean {
  const scrubbed = withoutTemplates(value);
  return scrubbed.split('\n').some((line) => {
    const match = HEADER_LINE.exec(line);
    return match !== null && CREDENTIAL_HEADERS.has(match[1].toLowerCase())
      && match[2].replace(SCHEME_PREFIX, '').trim().length >= 3;
  }) || TOKEN_SHAPES.some((pattern) => pattern.test(scrubbed)) || QUOTED_SECRET.test(scrubbed);
}

/** Copy with export-style secret detection; header objects preserve template references. */
function maskSecrets(value: unknown, isHeaderObject = false): unknown {
  if (typeof value === 'string') return looksSecret(value) ? REDACTED : value;
  if (Array.isArray(value)) return value.map((item) => maskSecrets(item));
  if (!isRecord(value)) return value;
  const out: Record<string, unknown> = {};
  for (const [key, v] of Object.entries(value)) {
    const lowerKey = key.toLowerCase();
    const isHeaders = lowerKey === 'headers';
    let secret = SECRET_CONFIG_KEYS.has(lowerKey) && v !== null && v !== undefined && v !== '';
    if (typeof v === 'string') {
      secret = isHeaderObject ? isLiteralSecretHeader(key, v) : secret || looksSecret(v)
        || (isHeaders && v.split('\n').some((line) => {
          const match = HEADER_LINE.exec(line);
          return match !== null && isLiteralSecretHeader(match[1], match[2]);
        }));
    }
    out[key] = secret ? REDACTED : maskSecrets(v, isHeaders);
  }
  return out;
}

function countRedacted(values: unknown[]): number {
  let count = 0;
  const visit = (v: unknown) => {
    if (v === REDACTED) count += 1;
    else if (Array.isArray(v)) v.forEach(visit);
    else if (isRecord(v)) Object.values(v).forEach(visit);
  };
  values.forEach(visit);
  return count;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNodeShape(value: unknown): value is Node {
  if (!isRecord(value)) return false;
  const { id, type, position, data } = value;
  return typeof id === 'string' && id.length > 0
    && typeof type === 'string' && NODE_TYPES.has(type)
    && isRecord(position) && Number.isFinite(position.x) && Number.isFinite(position.y)
    && isRecord(data);
}

function isEdgeShape(value: unknown): value is Edge {
  if (!isRecord(value)) return false;
  const { id, source, target } = value;
  return typeof id === 'string' && typeof source === 'string' && typeof target === 'string';
}
