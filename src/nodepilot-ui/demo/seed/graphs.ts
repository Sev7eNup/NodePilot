/**
 * Seed graphs, loaded from the workflow JSON the repository already ships.
 *
 * Nothing is authored here: these are the same definitions used for showcases and manual
 * testing, so the demo cannot drift into a graph shape the product never produces. Two
 * envelope forms exist and both are unwrapped below — a bare `{nodes,edges}` document and
 * the `nodepilot-workflow-export/v1` envelope with `workflows[].definition`.
 *
 * Imported with `?raw` rather than as JSON modules: it keeps the files out of the type
 * system (they are data, not types) and needs no `resolveJsonModule` in the product tsconfig.
 */
import sccmRaw from '../../../../samples/sccm-ad-provisioning.workflow.json?raw';
import diskSpaceRaw from '../../../../scripts/example-disk-space-watch-workflow.json?raw';
import windowsUpdateRaw from '../../../../scripts/example-windows-update-health-workflow.json?raw';
import serviceRecoveryRaw from '../../../../scripts/example-service-recovery-workflow.json?raw';
import tempCleanupRaw from '../../../../scripts/example-temp-file-cleanup-workflow.json?raw';
import guidedFileRaw from '../../../../scripts/example-guided-file-workflow.json?raw';
import type { GraphEdge, GraphNode } from '../state/world';

interface RawDefinition {
  nodes?: GraphNode[];
  edges?: GraphEdge[];
}

interface ExportEnvelope extends RawDefinition {
  schema?: string;
  workflows?: { name?: string; description?: string; definition?: RawDefinition }[];
}

/** One seed workflow before it becomes a `Workflow` row. */
export interface SeedGraph {
  /** Stable key, used to derive the workflow id. */
  key: string;
  name: string;
  description: string;
  /** Folder the workflow lives in, by folder key. */
  folderKey: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
}

/**
 * Authored metadata per source file. The names and descriptions are written here rather
 * than taken from the files: one source carries no name at all, and one describes itself
 * in German while the demo is presented in English.
 *
 * The order is load-bearing. `seed/executions.ts` indexes its cadence by position, and the
 * first and third slots are the ones whose newest run lands inside the Live-Ops window.
 */
const SOURCES: { key: string; raw: string; name: string; description: string; folderKey: string }[] = [
  {
    key: 'disk-space-watch',
    raw: diskSpaceRaw,
    name: 'Disk Space Watch',
    description:
      'Reads the free space on the system drive every morning. Below 10 GB it collects the ten largest folders and alerts the ops mailbox, below 25 GB it writes a warning, otherwise an all-clear entry.',
    folderKey: 'operations',
  },
  {
    key: 'sccm-provisioning',
    raw: sccmRaw,
    name: 'SCCM Package & AD Group Provisioning',
    description:
      'Creates an SCCM package, distributes it to all distribution points, sets security scopes, then provisions two nested AD groups. Every failure routes through a shared log node before the alert email.',
    folderKey: 'provisioning',
  },
  {
    key: 'windows-update-health',
    raw: windowsUpdateRaw,
    name: 'Windows Update Health Check',
    description:
      'Diagnoses the Windows Update state of a host: service status, registry result keys, installed hotfixes and the pending-reboot marker, classified into pending / degraded / healthy / stale.',
    folderKey: 'operations',
  },
  {
    key: 'service-recovery',
    raw: serviceRecoveryRaw,
    name: 'Service Recovery Watchdog',
    description:
      'Checks the Print Spooler every fifteen minutes. A stopped service is started, given time to settle and read back, and the outcome goes to the ops or the on-call mailbox.',
    folderKey: 'operations',
  },
  {
    key: 'temp-file-cleanup',
    raw: tempCleanupRaw,
    name: 'Temp File Cleanup',
    description:
      'Archives a temp folder into a ZIP, then checksums the archive while the aged-out originals are removed. Both branches converge on a waitAll junction before the summary mail.',
    folderKey: 'maintenance',
  },
  {
    key: 'guided-file', raw: guidedFileRaw, name: 'Configuration File Delivery', folderKey: 'operations',
    description: 'Guided example: create a configuration with PowerShell, copy it, and inspect the result. Registry, service and files are simulated. Environments: Test, Production, Protected (access denied).',
  },
];

/** Pulls the graph out of either envelope form. */
function unwrap(raw: string): RawDefinition {
  const parsed = JSON.parse(raw) as ExportEnvelope;
  const exported = parsed.workflows?.[0]?.definition;
  if (exported) return exported;
  return { nodes: parsed.nodes, edges: parsed.edges };
}

/**
 * Rewrites machine and credential references onto the demo's own ids.
 *
 * The source files were authored against other installations, so their GUIDs resolve to
 * nothing here and the designer would render "unknown machine" on most nodes. References
 * are remapped in first-seen order, which is deterministic across reloads.
 */
function remapReferences(nodes: GraphNode[], machineIds: string[], credentialIds: string[]): GraphNode[] {
  const machineMap = new Map<string, string>();
  const credentialMap = new Map<string, string>();

  const assign = (map: Map<string, string>, pool: string[], original: string): string => {
    const existing = map.get(original);
    if (existing) return existing;
    const next = pool[map.size % pool.length];
    map.set(original, next);
    return next;
  };

  return nodes.map((node) => {
    const data = node.data;
    if (!data) return node;
    const targetMachineId = typeof data.targetMachineId === 'string' && data.targetMachineId && machineIds.length > 0
      ? assign(machineMap, machineIds, data.targetMachineId)
      : data.targetMachineId ?? null;
    const rawCredentialId = (data as { credentialId?: string | null }).credentialId;
    const credentialId = typeof rawCredentialId === 'string' && rawCredentialId && credentialIds.length > 0
      ? assign(credentialMap, credentialIds, rawCredentialId)
      : rawCredentialId ?? null;
    return { ...node, data: { ...data, targetMachineId, credentialId } };
  });
}

/**
 * Inserts a `waitAll` junction in front of every node with more than one inbound edge.
 *
 * Explicit fan-in came after these definitions were written, so some of them wire several
 * predecessors straight into one activity. The designer and the SCOrch importer repair that
 * the same way when they load such a graph; doing it here keeps the seed publishable instead
 * of opening the demo on a canvas the linter rejects.
 */
function insertFanInJunctions(key: string, nodes: GraphNode[], edges: GraphEdge[]): { nodes: GraphNode[]; edges: GraphEdge[] } {
  const isJunction = (id: string) =>
    nodes.find((n) => n.id === id)?.data?.activityType?.toLowerCase() === 'junction';

  const inboundCount = new Map<string, number>();
  for (const edge of edges) inboundCount.set(edge.target, (inboundCount.get(edge.target) ?? 0) + 1);

  const needsJunction = [...inboundCount.entries()]
    .filter(([target, count]) => count > 1 && !isJunction(target))
    .map(([target]) => target);
  if (needsJunction.length === 0) return { nodes, edges };

  const extraNodes: GraphNode[] = [];
  let rewired = edges;

  for (const target of needsJunction) {
    const targetNode = nodes.find((n) => n.id === target);
    const junctionId = `${target}--fan-in`;
    extraNodes.push({
      id: junctionId,
      type: 'activity',
      position: {
        x: Math.round((targetNode?.position?.x ?? 0) - 220),
        y: Math.round(targetNode?.position?.y ?? 0),
      },
      data: {
        label: 'Wait for all',
        activityType: 'junction',
        targetMachineId: null,
        config: { mode: 'waitAll' },
      },
    });
    rewired = rewired.map((edge) => (edge.target === target ? { ...edge, target: junctionId } : edge));
    rewired = [
      ...rewired,
      { id: `${key}--${junctionId}--out`, source: junctionId, target, data: { label: 'On Success' } },
    ];
  }

  return { nodes: [...nodes, ...extraNodes], edges: rewired };
}

/** Builds every seed graph, with machine and credential references pointing at the demo world. */
export function seedGraphs(machineIds: string[], credentialIds: string[]): SeedGraph[] {
  return SOURCES.map((source) => {
    const definition = unwrap(source.raw);
    const repaired = insertFanInJunctions(
      source.key,
      remapReferences(definition.nodes ?? [], machineIds, credentialIds),
      definition.edges ?? [],
    );
    return {
      key: source.key,
      name: source.name,
      description: source.description,
      folderKey: source.folderKey,
      nodes: repaired.nodes,
      edges: repaired.edges,
    };
  });
}
