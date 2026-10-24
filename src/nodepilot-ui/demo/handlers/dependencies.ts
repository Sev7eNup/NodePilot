/** Static, bounded dependency reports derived from the mutable demo world. No seeded counts. */
import { dependencyKey, type DependencyObject, type DependencyKind, type DependencyLeg, type DependencyLocation, type DependencyReport, type DependencyItem } from '../../src/api/dependencies';
import { ACTIVITY_CATALOG } from '../../src/lib/activityCatalog.generated';
import { getWorld, type GraphNode, type GraphEdge } from '../state/world';
import { route, type RequestContext } from '../net/router';
import { json, notFound, conflict, badRequest } from '../net/respond';

type Leg = DependencyLeg & { defaultInput?: string; supplied?: string[] };
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
const str = (value: unknown) => typeof value === 'string' ? value : '';
// Revisions are cache identity only; no raw definition or value leaves this module.
function hash(value: unknown) {
  let h = 2166136261;
  for (const c of JSON.stringify(value)) h = Math.imul(h ^ c.charCodeAt(0), 16777619);
  return (h >>> 0).toString(16);
}
function snapshot() {
  const w = getWorld();
  const objects: DependencyObject[] = [];
  const add = (kind: DependencyKind, id: string, name: string, context: string, enabled: boolean | null, revision: unknown) => {
    const o = { kind, id, name, context, enabled, revision: hash(revision) }; objects.push(o); return o;
  };
  w.workflows.forEach(o => add('workflow', o.id, o.name, w.folders.find(f => f.id === o.folderId)?.path ?? '', o.isEnabled, o.definitionJson));
  w.machines.forEach(o => add('machine', o.id, o.name, o.hostname, null, [o.hostname, o.defaultCredentialId]));
  w.credentials.forEach(o => add('credential', o.id, o.name, '', null, o.name));
  w.globals.forEach(o => add('variable', o.id, o.name, '', null, o.updatedAt));
  w.customActivities.forEach(o => add('customActivity', o.id, o.name, o.key, o.isEnabled, [o.scriptTemplate, o.inputs, o.version]));
  const find = (kind: DependencyKind, id: string) => objects.find(o => o.kind === kind && o.id === id);
  const links: Leg[] = [];
  const problems: DependencyReport['problems'] = [];
  const limitations = new Set<string>();
  const location = (source: DependencyObject, node: GraphNode | null, field: string): DependencyLocation => ({
    sourceKind: source.kind, sourceId: source.id, nodeId: node?.id ?? null, label: node?.data?.label ?? source.name,
    field, disabled: node?.data?.disabled ?? false, revision: source.revision, viaMachineId: null,
  });
  const problem = (kind: string, status: string, loc: DependencyLocation) => problems.push({ kind, status, location: loc });
  function link(source: DependencyObject, target: DependencyObject, relation: string, loc: DependencyLocation, extra: Partial<Leg> = {}) {
    const existing = links.find(l => l.source === source && l.target === target && l.relation === relation && l.defaultInput === extra.defaultInput && JSON.stringify(l.supplied) === JSON.stringify(extra.supplied));
    if (existing) { if (!existing.locations.some(p => p.nodeId === loc.nodeId && p.field === loc.field)) existing.locations.push(loc); existing.locationCount = existing.locations.length; }
    else links.push({ source, target, relation, locations: [loc], locationCount: 1, ...extra });
  }
  function resolve(kind: DependencyKind, value: unknown, loc: DependencyLocation) {
    const raw = str(value).trim();
    if (!raw) { problem(kind, 'unresolved', loc); return; }
    if (raw.includes('{{')) { problem(kind, 'dynamic', loc); return; }
    const candidates = objects.filter(o => o.kind === kind);
    const exact = candidates.filter(o => o.id === raw || o.name === raw);
    const matches = exact.length ? exact : candidates.filter(o => o.name.toLowerCase() === raw.toLowerCase());
    if (matches.length === 1) return matches[0];
    problem(kind, matches.length ? 'ambiguous' : 'unresolved', loc);
  }
  function globals(source: DependencyObject, value: unknown, loc: DependencyLocation, defaultInput?: string) {
    if (typeof value === 'string') {
      for (const match of value.matchAll(/\{\{globals\.([A-Za-z0-9_-]+)\}\}/g)) {
        const target = objects.find(o => o.kind === 'variable' && o.name === match[1]);
        if (target) link(source, target, defaultInput ? 'usesDefaultVariable' : 'usesVariable', loc, { defaultInput });
        else problem('variable', 'unresolved', loc);
      }
    } else if (Array.isArray(value)) value.forEach((v, i) => globals(source, v, { ...loc, field: `${loc.field}[${i}]` }, defaultInput));
    else Object.entries(record(value)).filter(([k]) => !k.startsWith('__')).forEach(([k, v]) => globals(source, v, { ...loc, field: `${loc.field}.${k}` }, defaultInput));
  }
  function binding(source: DependencyObject, node: GraphNode, target: unknown, credential: unknown, serviceIdentity = false, field = 'data') {
    const loc = location(source, node, `${field}.targetMachineId`);
    const raw = str(target).trim();
    let machine: typeof w.machines[number] | undefined;
    if (!raw || ['localhost', '.', '127.0.0.1', '::1'].includes(raw.toLowerCase())) problem('machine', 'local', loc);
    else if (raw.includes('{{')) problem('machine', 'dynamic', loc);
    else {
      const candidates = w.machines.filter(m => m.id === raw || m.name.toLowerCase() === raw.toLowerCase() || m.hostname.toLowerCase() === raw.toLowerCase());
      if (candidates.length === 1) { machine = candidates[0]; link(source, find('machine', machine.id)!, 'usesMachine', loc); }
      else problem('machine', candidates.length ? 'ambiguous' : /^[\da-f-]{36}$/i.test(raw) ? 'unresolved' : 'external', loc);
    }
    if (serviceIdentity) return;
    const cred = str(credential).trim() || machine?.defaultCredentialId;
    if (!cred) return;
    const credLoc = { ...loc, field: `${field}.credentialId`, viaMachineId: str(credential).trim() ? null : machine?.id ?? null };
    const resolved = resolve('credential', cred, credLoc);
    if (resolved) link(source, resolved, credLoc.viaMachineId ? 'inheritsCredential' : 'usesCredential', credLoc);
  }
  for (const machine of w.machines) {
    if (machine.defaultCredentialId) {
      const source = find('machine', machine.id)!; const loc = location(source, null, 'defaultCredentialId');
      const target = resolve('credential', machine.defaultCredentialId, loc);
      if (target) link(source, target, 'defaultCredential', loc);
    }
  }
  for (const custom of w.customActivities) {
    const source = find('customActivity', custom.id)!;
    globals(source, custom.scriptTemplate, location(source, null, 'scriptTemplate'));
    custom.inputs.forEach(input => globals(source, input.default, location(source, null, 'inputParameters.default'), input.name));
  }
  for (const workflow of w.workflows) {
    const source = find('workflow', workflow.id)!;
    let def: { nodes: GraphNode[]; edges: GraphEdge[] };
    try { def = JSON.parse(workflow.definitionJson); if (!Array.isArray(def.nodes) || !Array.isArray(def.edges)) throw new Error(); }
    catch { problem('workflow', 'invalid', location(source, null, 'definition')); limitations.add('invalidDefinition'); continue; }
    for (const node of def.nodes) {
      const data = record(node.data); const config = record(data.config); const type = str(data.activityType) || node.type || '';
      const known = ACTIVITY_CATALOG.find(a => a.type === type);
      const custom = type.startsWith('custom:') ? w.customActivities.find(c => c.id === config.__customDefinitionId && c.key === (config.__customKey ?? type.slice(7))) : undefined;
      if (type.startsWith('custom:')) {
        const loc = location(source, node, 'data.config.__customDefinitionId');
        if (custom) link(source, find('customActivity', custom.id)!, 'usesCustom', loc, { supplied: Object.keys(config).filter(k => config[k] != null) });
        else problem('customActivity', 'unresolved', loc);
      }
      if (!known && !custom) { if (!['stickyNote', 'group'].includes(type)) limitations.add('unsupportedActivity'); continue; }
      globals(source, config, location(source, node, 'data.config'));
      globals(source, data.targetMachineId, location(source, node, 'data.targetMachineId'));
      globals(source, data.credentialId, location(source, node, 'data.credentialId'));
      if (known?.isRemote || custom?.runsRemote) binding(source, node, data.targetMachineId, data.credentialId);
      const callField = type === 'startWorkflow' ? 'workflowNameOrId' : type === 'forEach' ? 'childWorkflowNameOrId' : null;
      if (callField) {
        const loc = location(source, node, `data.config.${callField}`); const target = resolve('workflow', config[callField], loc);
        if (target) link(source, target, 'calls', loc);
      }
      if (type === 'aiAgent' || type === 'aiAgentTeam') {
        const agents = Array.isArray(config.members) ? config.members : [config.agent];
        agents.forEach((value, i) => {
          const agent = record(value); const field = Array.isArray(config.members) ? `data.config.members[${i}]` : 'data.config.agent';
          binding(source, node, agent.targetMachineId ?? data.targetMachineId, agent.credentialId ?? data.credentialId, agent.useServiceIdentity === true, field);
          if (Array.isArray(agent.skillIds) && agent.skillIds.length) limitations.add('agentResources');
          if (Array.isArray(agent.tools)) agent.tools.forEach((value, index) => {
            const tool = record(value);
            if (tool.name === 'mcp') limitations.add('agentResources');
            if (tool.name === 'workflow_run' && Array.isArray(tool.workflowIds)) tool.workflowIds.forEach(id => {
              const loc = location(source, node, `${field}.tools[${index}].workflowIds`); const target = resolve('workflow', id, loc);
              if (target) link(source, target, 'mayCall', loc);
            });
          });
        });
      }
    }
    def.edges.forEach(edge => globals(source, edge.data?.condition, { ...location(source, def.nodes.find(n => n.id === edge.source) ?? null, `edges[${edge.id}].condition`), disabled: edge.data?.disabled ?? false }));
  }
  return { objects, links, problems, limitations: [...limitations], revision: hash([objects, links, problems]) };
}

function analyze(root: DependencyObject, links: Leg[], incoming: boolean) {
  const items = new Map<string, DependencyItem>();
  const queue = [{ object: root, path: [] as Leg[], visited: new Set([dependencyKey(root)]) }];
  const expanded = new Set<string>(); let scanned = 0; let complete = true;
  while (queue.length && scanned <= 20_000) {
    const state = queue.shift()!;
    for (const edge of links.filter(l => dependencyKey(incoming ? l.target : l.source) === dependencyKey(state.object))) {
      if (++scanned > 20_000) { complete = false; break; }
      const object = incoming ? edge.source : edge.target; const key = dependencyKey(object);
      if (state.visited.has(key)) continue;
      const path = incoming ? [edge, ...state.path] : [...state.path, edge];
      const left = incoming ? edge : state.path.at(-1); const right = incoming ? state.path[0] : edge;
      if (state.path.length && left && right && !['calls', 'mayCall'].includes(left.relation) && !(left.relation === 'usesCustom' && (right.relation === 'usesVariable' || right.relation === 'usesDefaultVariable' && !left.supplied?.includes(right.defaultInput!)))) continue;
      if (path.length > 12) { complete = false; continue; }
      const possible = path.some(l => l.relation === 'mayCall'); const old = items.get(key);
      if (!old) items.set(key, { object, distance: path.length, possible, paths: [{ links: path }] });
      else { if (old.paths.length < 3) old.paths.push({ links: path }); if (old.possible && !possible) { old.possible = false; old.distance = path.length; old.paths.unshift({ links: path }); old.paths = old.paths.slice(0, 3); } }
      const expansion = `${key}:${links.indexOf(edge)}:${possible}`;
      if (!expanded.has(expansion)) { expanded.add(expansion); queue.push({ object, path, visited: new Set([...state.visited, key]) }); }
    }
  }
  return { items: [...items.values()].sort((a, b) => Number(a.possible) - Number(b.possible) || Number(a.distance > 1) - Number(b.distance > 1) || a.object.name.localeCompare(b.object.name)), complete };
}
function page<T>(items: T[], ctx: RequestContext, revision: string) {
  const params = new URLSearchParams(ctx.query); params.delete('cursor'); params.delete('limit');
  const stamp = hash([revision, ctx.path, params.toString()]); const cursor = ctx.query.get('cursor');
  const offset = cursor ? Number(cursor.split('.')[1]) : 0;
  if (cursor && (cursor.split('.')[0] !== stamp || !Number.isInteger(offset) || offset < 0)) return conflict('DEPENDENCIES_CHANGED', 'Refresh the dependency report.');
  const limit = Math.max(1, Math.min(100, Number(ctx.query.get('limit')) || 50));
  return { items: items.slice(offset, offset + limit), nextCursor: offset + limit < items.length ? `${stamp}.${offset + limit}` : null };
}
function report(ctx: RequestContext, evidence: boolean) {
  const graph = snapshot(); const root = graph.objects.find(o => o.kind === ctx.params.kind && o.id === ctx.params.id);
  if (!root) return notFound('Object');
  const direction = ctx.query.get('direction') ?? 'usedBy';
  if (!['usedBy', 'uses'].includes(direction)) return badRequest('INVALID_DIRECTION', 'Invalid direction.');
  const result = analyze(root, graph.links, direction === 'usedBy');
  if (evidence) {
    const item = result.items.find(i => i.object.kind === ctx.query.get('relatedKind') && i.object.id === ctx.query.get('relatedId'));
    if (!item) return notFound('Relationship');
    const locations = [...new Map(item.paths.flatMap(p => p.links.flatMap(l => l.locations)).map(l => [JSON.stringify(l), l])).values()];
    const paged = page(locations, ctx, graph.revision);
    return paged instanceof Response ? paged : json({ ...paged, paths: item.paths, total: locations.length, analysisRevision: graph.revision });
  }
  const paged = page(result.items, ctx, graph.revision);
  return paged instanceof Response ? paged : json({ ...paged, object: root, direction, generatedAt: new Date().toISOString(), analysisRevision: graph.revision, complete: result.complete,
    limitations: result.complete ? graph.limitations : [...graph.limitations, 'analysisBudget'],
    counts: { direct: result.items.filter(i => !i.possible && i.distance === 1).length, indirect: result.items.filter(i => !i.possible && i.distance > 1).length, possible: result.items.filter(i => i.possible).length },
    problems: graph.problems.filter(p => p.location.sourceKind === root.kind && p.location.sourceId === root.id) });
}
export const dependencyRoutes = [
  route('GET', '/dependencies/objects', ctx => {
    const graph = snapshot(); const q = (ctx.query.get('q') ?? '').toLowerCase(); const kind = ctx.query.get('kind');
    const state = ctx.query.get('state'); const sort = ctx.query.get('sort') ?? 'name';
    if (state && !['problems', 'unused'].includes(state) || !['name', 'usage'].includes(sort)) return badRequest('INVALID_FILTER', 'Invalid filter.');
    const users = new Map<string, Set<string>>();
    graph.links.filter(l => l.relation !== 'mayCall' && dependencyKey(l.source) !== dependencyKey(l.target))
      .forEach(l => users.set(dependencyKey(l.target), (users.get(dependencyKey(l.target)) ?? new Set()).add(dependencyKey(l.source))));
    const problems = new Map<string, number>();
    graph.problems.filter(p => ['unresolved', 'ambiguous', 'invalid'].includes(p.status))
      .forEach(p => { const key = `${p.location.sourceKind}:${p.location.sourceId}`; problems.set(key, (problems.get(key) ?? 0) + 1); });
    const direct = (o: DependencyObject) => users.get(dependencyKey(o))?.size ?? 0;
    // Same rules as the API: workflows start from triggers and are never listed as unused.
    const objects = graph.objects.filter(o => (!kind || o.kind === kind) && `${o.name} ${o.context} ${o.id}`.toLowerCase().includes(q)
      && (state === 'problems' ? problems.has(dependencyKey(o)) : state === 'unused' ? o.kind !== 'workflow' && direct(o) === 0 : true))
      .sort((a, b) => (sort === 'usage' ? direct(b) - direct(a) : 0) || a.name.localeCompare(b.name));
    const paged = page(objects, ctx, graph.revision);
    return paged instanceof Response ? paged : json({ ...paged, analysisRevision: graph.revision, items: paged.items.map(o => ({
      object: o, usedByDirect: direct(o), usedByIndirect: analyze(o, graph.links, true).items.filter(i => !i.possible && i.distance > 1).length,
      problems: problems.get(dependencyKey(o)) ?? 0,
    })) });
  }),
  route('GET', '/dependencies/:kind/:id/evidence', ctx => report(ctx, true)),
  route('GET', '/dependencies/:kind/:id', ctx => report(ctx, false)),
];
