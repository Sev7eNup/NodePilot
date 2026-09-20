/**
 * Custom activities (the UI calls them Custom Nodes).
 *
 * Two shapes, not one. The list endpoint serves the palette catalog — the facts the designer
 * needs, without the script. The single-item read serves the full definition, which is what the
 * edit dialog loads. Serving only the first is what made "Edit" throw: the fallback answered
 * `{}`, the form took `name` as undefined, and `form.name.trim()` took the page down.
 *
 * Editing works in memory, the same way workflows and machines do. Only importing needs a real
 * server, because that parses an uploaded archive.
 */
import { route, type Route } from '../net/router';
import { conflict, download, json, noContent, notFound, notInDemo } from '../net/respond';
import { getWorld } from '../state/world';
import { runtimeId } from '../state/ids';
import { DEMO_USER } from '../seed/entities';
import { fullDefinitionOf, type DemoCustomActivityDefinition } from '../seed/customActivities';

/** The palette view: everything except the script and the engine knobs. */
function catalogEntry(definition: DemoCustomActivityDefinition) {
  return {
    id: definition.id,
    key: definition.key,
    type: `custom:${definition.key}`,
    name: definition.name,
    description: definition.description,
    icon: definition.icon,
    color: definition.color,
    runsRemote: definition.runsRemote,
    timeout: 'always',
    inputs: definition.inputs,
    outputs: definition.outputs,
    isEnabled: definition.isEnabled,
    version: definition.version,
  };
}

interface SaveBody {
  key?: string;
  name?: string;
  description?: string | null;
  icon?: string;
  color?: string | null;
  scriptTemplate?: string;
  engine?: string;
  runsRemote?: boolean;
  isolated?: boolean;
  defaultTimeoutSeconds?: number | null;
  successExitCodes?: string | null;
  inputs?: DemoCustomActivityDefinition['inputs'];
  outputs?: DemoCustomActivityDefinition['outputs'];
  concurrencyToken?: string | null;
}

/**
 * Stores the definition as it stands, before a save or a rollback overwrites it.
 *
 * The history holds only previous versions — the live definition is never in it. That is the
 * contract the versions dialog reads: every listed row is a valid rollback target.
 */
function snapshotVersion(definition: DemoCustomActivityDefinition): void {
  const history = getWorld().customActivityVersions;
  const rows = history.get(definition.id) ?? [];
  rows.unshift({ ...definition, inputs: [...definition.inputs], outputs: [...definition.outputs] });
  history.set(definition.id, rows);
}

function applySave(definition: DemoCustomActivityDefinition, body: SaveBody | undefined): void {
  if (!body) return;
  snapshotVersion(definition);
  if (typeof body.name === 'string' && body.name.trim()) definition.name = body.name.trim();
  if (body.description !== undefined) definition.description = body.description;
  if (typeof body.icon === 'string' && body.icon) definition.icon = body.icon;
  if (body.color !== undefined) definition.color = body.color;
  if (typeof body.scriptTemplate === 'string') definition.scriptTemplate = body.scriptTemplate;
  if (typeof body.engine === 'string') definition.engine = body.engine;
  if (typeof body.runsRemote === 'boolean') definition.runsRemote = body.runsRemote;
  if (typeof body.isolated === 'boolean') definition.isolated = body.isolated;
  if (body.defaultTimeoutSeconds !== undefined) definition.defaultTimeoutSeconds = body.defaultTimeoutSeconds;
  if (body.successExitCodes !== undefined) definition.successExitCodes = body.successExitCodes;
  if (Array.isArray(body.inputs)) definition.inputs = body.inputs;
  if (Array.isArray(body.outputs)) definition.outputs = body.outputs;
  definition.version += 1;
  definition.updatedAt = new Date().toISOString();
  definition.updatedBy = DEMO_USER.username;
  // A fresh token per save, so the UI's optimistic-concurrency check behaves like the product's.
  definition.concurrencyToken = runtimeId('custom-token');
}

function find(id: string): DemoCustomActivityDefinition | undefined {
  return getWorld().customActivities.find((c) => c.id === id);
}

export const customActivityRoutes: Route[] = [
  // Registered before `/custom-activities/:id` so the literal paths win.
  route('GET', '/custom-activities/export', () =>
    download(
      JSON.stringify(
        {
          schema: 'nodepilot-custom-activities/v1',
          exportedAt: new Date().toISOString(),
          activities: getWorld().customActivities.map(fullDefinitionOf),
        },
        null,
        2,
      ),
      'custom-nodes.npca.json',
    )),

  route('POST', '/custom-activities/import', () => notInDemo('Importing custom activities')),

  route('GET', '/custom-activities', (ctx) => {
    const includeDisabled = ctx.query.get('includeDisabled') === 'true';
    const rows = getWorld().customActivities.filter((c) => includeDisabled || c.isEnabled);
    return json(rows.map(catalogEntry));
  }),

  route('POST', '/custom-activities', async (ctx) => {
    const body = await ctx.body<SaveBody>();
    const key = body?.key?.trim();
    if (!key) return conflict('CUSTOM_ACTIVITY_KEY_REQUIRED', 'A key is required.');
    if (getWorld().customActivities.some((c) => c.key === key)) {
      return conflict('CUSTOM_ACTIVITY_KEY_TAKEN', `The key "${key}" is already in use.`);
    }
    const now = new Date().toISOString();
    const definition: DemoCustomActivityDefinition = {
      id: runtimeId('custom-activity'),
      key,
      name: body?.name?.trim() || key,
      description: body?.description ?? null,
      icon: body?.icon || 'extension',
      color: body?.color ?? null,
      runsRemote: body?.runsRemote ?? true,
      inputs: body?.inputs ?? [],
      outputs: body?.outputs ?? [],
      // New definitions start disabled — a draft, as the product's governance requires.
      isEnabled: false,
      version: 1,
      scriptTemplate: body?.scriptTemplate ?? '',
      engine: body?.engine ?? 'powershell',
      isolated: body?.isolated ?? false,
      memoryLimitMb: null,
      maxProcesses: null,
      defaultTimeoutSeconds: body?.defaultTimeoutSeconds ?? null,
      successExitCodes: body?.successExitCodes ?? null,
      concurrencyToken: runtimeId('custom-token'),
      updatedAt: now,
      updatedBy: DEMO_USER.username,
    };
    getWorld().customActivities.push(definition);
    return json(fullDefinitionOf(definition), 201);
  }),

  // The edit dialog loads this and copies every field into its form.
  route('GET', '/custom-activities/:id', (ctx) => {
    const definition = find(ctx.params.id);
    return definition ? json(fullDefinitionOf(definition)) : notFound('Custom activity');
  }),

  route('PUT', '/custom-activities/:id', async (ctx) => {
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    applySave(definition, await ctx.body<SaveBody>());
    return json(fullDefinitionOf(definition));
  }),

  route('DELETE', '/custom-activities/:id', (ctx) => {
    const world = getWorld();
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    if (definition.isEnabled) {
      // Mirrors the product: an enabled definition is deleted only after it is disabled.
      return conflict('CUSTOM_ACTIVITY_ENABLED', 'Disable the custom activity before deleting it.');
    }
    world.customActivities = world.customActivities.filter((c) => c.id !== definition.id);
    return noContent();
  }),

  route('POST', '/custom-activities/:id/enable', (ctx) => {
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    definition.isEnabled = true;
    return json(fullDefinitionOf(definition));
  }),

  route('POST', '/custom-activities/:id/disable', (ctx) => {
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    definition.isEnabled = false;
    return json(fullDefinitionOf(definition));
  }),

  route('GET', '/custom-activities/:id/versions', (ctx) => {
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    const history = getWorld().customActivityVersions.get(definition.id) ?? [];
    return json(
      history.map((version) => ({
        version: version.version,
        name: version.name,
        description: version.description,
        engine: version.engine,
        runsRemote: version.runsRemote,
        createdAt: version.updatedAt,
        createdBy: version.updatedBy,
        changeNote: null,
      })),
    );
  }),

  route('POST', '/custom-activities/:id/rollback/:version', (ctx) => {
    const definition = find(ctx.params.id);
    if (!definition) return notFound('Custom activity');
    const history = getWorld().customActivityVersions.get(definition.id) ?? [];
    const target = history.find((v) => v.version === Number(ctx.params.version));
    // Reporting success for a version that was never stored is worse than refusing: the
    // dialog would close and the definition would be unchanged.
    if (!target) return notFound(`Version ${ctx.params.version}`);

    // Snapshot the live state first, then restore. A rollback moves forward to a new version
    // carrying old content — it does not rewind the history.
    snapshotVersion(definition);
    Object.assign(definition, target, {
      id: definition.id,
      key: definition.key,
      isEnabled: definition.isEnabled,
      version: definition.version + 1,
      updatedAt: new Date().toISOString(),
      updatedBy: DEMO_USER.username,
      concurrencyToken: runtimeId('custom-token'),
    });
    return json(fullDefinitionOf(definition));
  }),
];
