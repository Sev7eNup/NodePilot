import type { Node, Edge } from '@xyflow/react';

/**
 * Matches `{{name.field…}}` references — identical to the runtime resolver pattern in
 * `WorkflowEngine.ResolveVariables`. Captured group is the head (`name`), which is what
 * tells us "this references step `name`'s output". The `.field…` tail is intentionally
 * permissive (`.output`, `.error`, `.param.foo`, `.param.foo.bar`).
 *
 * Lazy-rebuilt per scan because RegExp objects with `g` flag carry mutable `lastIndex`
 * state — sharing a single instance across calls invites off-by-one bugs.
 */
const TEMPLATE_REGEX_SOURCE = /\{\{\s*([a-zA-Z0-9_-]+)(\.[a-zA-Z0-9_.-]+)?\s*\}\}/g;

/**
 * Heads that the engine injects at runtime — never count them as upstream-step refs.
 * Limited to `globals` and `manual`: those are the only two namespaces the engine
 * actually populates ({@link VariableResolver.BuildStepVariables}). There is no
 * `trigger.` / `webhook.` namespace — webhook payload is exposed under `manual.*` keys.
 */
const RUNTIME_HEADS = new Set(['globals', 'manual']);

/**
 * Recursively walks an arbitrary node-config payload and returns every `{{head.…}}` head
 * found anywhere in string values (top-level or nested in objects/arrays). Heads are
 * deduplicated; runtime prefixes (globals/manual) are filtered out so callers can
 * directly intersect with upstream-step IDs / outputVariables.
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
 * One-shot computation of `nodeId → referenced heads`. Cheap enough to run on every
 * `nodes` change for the data-flow overlay (≤ low-100s of nodes in practice; each
 * config payload is small and stringly-shallow).
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
 * Returns the canonical "head" a step exposes to downstream — its `outputVariable` if set,
 * otherwise the node id (matches WorkflowEngine.ResolveVariables fallback exactly).
 */
function exposedHead(node: Node): string {
  const data = (node.data as Record<string, unknown> | undefined) ?? {};
  const ov = (data.outputVariable as string | undefined)?.trim();
  return ov && ov.length > 0 ? ov : node.id;
}

/**
 * For each edge in the workflow, returns the list of variable heads that actually flow
 * across it — i.e. heads exposed by the source step (or any of its already-flowing upstream
 * heads, propagated transitively along edge direction) AND referenced by the target step
 * or any of its successors.
 *
 * Why transitive propagation: a chain `A → B → C` where C does `{{A.output}}` should still
 * highlight the A→B edge as flowing — without that, only the directly-consuming edge would
 * light up and the chain visualisation would be misleading.
 *
 * Disabled edges are skipped (they don't carry runtime data). The result is a `Map<edgeId,
 * heads[]>` so the renderer can look up O(1) per edge.
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

  // Build adjacency lists, skipping disabled edges (their data wouldn't move at runtime).
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

  // Per-node BFS for forward + backward reachability. Iterative DP with memo + cycle marker
  // gives wrong answers on cycles (visit order leaks into the cached partial result), so we
  // run a fresh BFS per query. Workflows top out at low-100s of nodes so the O(N · E) total
  // is well below noticeable cost — and the result is always exact regardless of cycles.
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

  // Memoise per node — multiple edges sharing a source/target endpoint pay only once.
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
