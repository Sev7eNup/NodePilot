import type { Node, Edge } from '@xyflow/react';

/**
 * Matches `{{name.field}}` references, the same pattern the runtime resolver in
 * `WorkflowEngine.ResolveVariables` uses. The captured group is the head (`name`), the step
 * whose output is referenced; the tail is deliberately permissive (`.output`, `.error`,
 * `.param.foo`, `.param.foo.bar`). Callers rebuild the regex per scan because a `g`-flagged
 * RegExp carries mutable `lastIndex` state that a shared instance would leak across calls.
 */
const TEMPLATE_REGEX_SOURCE = /\{\{\s*([a-zA-Z0-9_-]+)(\.[a-zA-Z0-9_.-]+)?\s*\}\}/g;

/**
 * Heads the engine injects at runtime; they never count as upstream-step references.
 * `globals` and `manual` are the only namespaces the engine populates
 * ({@link VariableResolver.BuildStepVariables}). There is no `trigger.` or `webhook.`
 * namespace: the webhook payload is exposed under `manual.*` keys.
 */
const RUNTIME_HEADS = new Set(['globals', 'manual']);

/**
 * Recursively walks an arbitrary node-config payload and returns every `{{head}}` head found
 * in string values, at the top level or nested in objects and arrays. Heads are deduplicated
 * and runtime prefixes (globals, manual) are filtered out, so callers can intersect the result
 * directly with upstream step ids and outputVariables.
 */
export function findReferencedVariables(value: unknown): string[] {
  const heads = new Set<string>();
  collect(value, heads);
  return Array.from(heads);
}

function collect(value: unknown, heads: Set<string>): void {
  if (value === null || value === undefined) return;
  if (typeof value === 'string') {
    if (!value.includes('{{')) return;
    const re = new RegExp(TEMPLATE_REGEX_SOURCE.source, 'g');
    let m: RegExpExecArray | null;
    while ((m = re.exec(value))) {
      const head = m[1];
      if (!RUNTIME_HEADS.has(head)) heads.add(head);
    }
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) collect(item, heads);
    return;
  }
  if (typeof value === 'object') {
    for (const v of Object.values(value as Record<string, unknown>)) collect(v, heads);
  }
}

/**
 * Returns the head names referenced by everything reachable inside `node.data.config`
 * plus the surface-level `targetMachineId` / `credentialId` (those can also be templated).
 */
export function variablesUsedByNode(node: Node): string[] {
  const data = (node.data as Record<string, unknown> | undefined) ?? {};
  const heads = new Set<string>();
  collect(data.config, heads);
  collect(data.targetMachineId, heads);
  collect(data.credentialId, heads);
  return Array.from(heads);
}

/**
 * Maps each node id to the heads it references. Cheap enough to run on every `nodes` change
 * for the data-flow overlay, since workflows stay small and config payloads are shallow.
 */
export function usedVariablesPerNode(nodes: Node[]): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const n of nodes) {
    const heads = variablesUsedByNode(n);
    if (heads.length > 0) out.set(n.id, heads);
  }
  return out;
}

/**
 * Returns the head a step exposes downstream: its `outputVariable` if set, otherwise the node
 * id, matching the fallback in WorkflowEngine.ResolveVariables.
 */
function exposedHead(node: Node): string {
  const data = (node.data as Record<string, unknown> | undefined) ?? {};
  const ov = (data.outputVariable as string | undefined)?.trim();
  return ov && ov.length > 0 ? ov : node.id;
}

/**
 * For each edge, returns the variable heads that flow across it: heads exposed by the source
 * step or any of its upstream steps, propagated along edge direction, that are referenced by
 * the target step or any of its successors. The transitive part keeps a whole chain highlighted
 * when a later step reads an earlier step's output, instead of lighting only the consuming edge.
 *
 * Disabled edges are skipped, since they carry no runtime data. The result is a `Map<edgeId,
 * heads[]>` so the renderer can look up each edge in O(1).
 */
export function computeFlowingVariablesPerEdge(
  nodes: Node[],
  edges: Edge[],
): Map<string, string[]> {
  const result = new Map<string, string[]>();
  if (nodes.length === 0 || edges.length === 0) return result;

  const used = usedVariablesPerNode(nodes);
  const headByNode = new Map<string, string>();
  for (const n of nodes) headByNode.set(n.id, exposedHead(n));

  // Build adjacency lists, skipping disabled edges since their data does not move at runtime.
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const n of nodes) {
    outgoing.set(n.id, []);
    incoming.set(n.id, []);
  }
  const activeEdges: Edge[] = [];
  for (const e of edges) {
    const disabled = (e.data as Record<string, unknown> | undefined)?.disabled;
    if (disabled) continue;
    if (!outgoing.has(e.source) || !incoming.has(e.target)) continue;
    outgoing.get(e.source)!.push(e.target);
    incoming.get(e.target)!.push(e.source);
    activeEdges.push(e);
  }

  // A fresh per-node search for forward and backward reachability. Memoised DP with a cycle
  // marker returns wrong answers on cycles, because visit order leaks into the cached partial
  // result. Running a search per query stays exact, and workflows are small enough for the cost.
  const reachForward = (start: string): Set<string> => {
    const reached = new Set<string>();
    const stack = [start];
    while (stack.length) {
      const cur = stack.pop()!;
      if (reached.has(cur)) continue;
      reached.add(cur);
      for (const succ of outgoing.get(cur) ?? []) stack.push(succ);
    }
    return reached;
  };
  const reachBackward = (start: string): Set<string> => {
    const reached = new Set<string>();
    const stack = [start];
    while (stack.length) {
      const cur = stack.pop()!;
      if (reached.has(cur)) continue;
      reached.add(cur);
      for (const pred of incoming.get(cur) ?? []) stack.push(pred);
    }
    return reached;
  };

  // Memoise per node so multiple edges sharing a source or target endpoint pay only once.
  const downstreamCache = new Map<string, Set<string>>();
  const upstreamCache = new Map<string, Set<string>>();
  const downstreamUsageOf = (id: string): Set<string> => {
    const cached = downstreamCache.get(id);
    if (cached) return cached;
    const set = new Set<string>();
    for (const node of reachForward(id)) {
      const heads = used.get(node);
      if (heads) for (const h of heads) set.add(h);
    }
    downstreamCache.set(id, set);
    return set;
  };
  const upstreamHeadsOf = (id: string): Set<string> => {
    const cached = upstreamCache.get(id);
    if (cached) return cached;
    const set = new Set<string>();
    for (const node of reachBackward(id)) {
      set.add(headByNode.get(node) ?? node);
    }
    upstreamCache.set(id, set);
    return set;
  };

  for (const e of activeEdges) {
    const sourceUpstreamHeads = upstreamHeadsOf(e.source);
    const targetDownstreamUsage = downstreamUsageOf(e.target);
    const flowing: string[] = [];
    for (const head of sourceUpstreamHeads) {
      if (targetDownstreamUsage.has(head)) flowing.push(head);
    }
    if (flowing.length > 0) {
      flowing.sort((a, b) => a.localeCompare(b));
      result.set(e.id, flowing);
    }
  }

  return result;
}
