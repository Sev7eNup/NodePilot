/**
 * The demo's in-memory world.
 *
 * Everything the fake backend serves lives here. It is per-tab and never persisted, so a
 * reload restores the seed and two visitors never share state.
 *
 * Only `seed/entities.ts` and `seed/graphs.ts` hold authored facts. Executions, dashboard
 * aggregates and live-run results are derived from those, so the numbers on one screen can
 * never contradict another.
 */
import type {
  Credential,
  ManagedMachine,
  StepExecution,
  UserRow,
  Workflow,
  WorkflowExecution,
} from '../../src/types/api';
import type { SharedFolder, SharedFolderPermission } from '../../src/api/sharedFolders';
import type { GlobalFolder } from '../../src/api/globalFolders';
import type { DemoCustomActivityDefinition } from '../seed/customActivities';
import { resetRuntimeIds } from './ids';

/** Mirrors the row shape `GlobalVariablesPage` reads. */
export interface DemoGlobalVariable {
  id: string;
  name: string;
  value: string | null;
  isSecret: boolean;
  description: string | null;
  folderId: string;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
}

/**
 * Mirrors the row shape `MaintenanceWindowsPage` reads. The page keeps its own local type,
 * so there is nothing to import.
 */
export interface DemoMaintenanceWindow {
  id: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  mode: 'Blackout' | 'AllowOnly';
  scopeKind: 'Global' | 'Folders' | 'Workflows';
  recurrence: 'OneTime' | 'Weekly' | 'Cron';
  oneTimeStartUtc: string | null;
  oneTimeEndUtc: string | null;
  weeklyDaysMask: number;
  weeklyStartMinuteOfDay: number | null;
  weeklyEndMinuteOfDay: number | null;
  cronExpression: string | null;
  durationMinutes: number | null;
  timeZoneId: string;
  targets: { targetKind: 'Folder' | 'Workflow'; targetId: string }[];
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
}

/** Mirrors the row shape `WorkflowDiffModal` reads from `/workflows/{id}/versions`. */
export interface DemoWorkflowVersion {
  version: number;
  isCurrent: boolean;
  createdAt: string | null;
  createdBy: string | null;
  changeNote: string | null;
  /** Full definition, served by `/workflows/{id}/versions/{version}`. */
  definitionJson: string;
}

export interface DemoWorld {
  workflows: Workflow[];
  /** workflowId -> version history, newest first. */
  versions: Map<string, DemoWorkflowVersion[]>;
  executions: WorkflowExecution[];
  /** executionId -> steps, in start order. */
  steps: Map<string, StepExecution[]>;
  machines: ManagedMachine[];
  credentials: Credential[];
  globals: DemoGlobalVariable[];
  globalFolders: GlobalFolder[];
  folders: SharedFolder[];
  /** Folder-RBAC grants, keyed by their own id and carrying the folder they belong to. */
  folderPermissions: SharedFolderPermission[];
  customActivities: DemoCustomActivityDefinition[];
  /** customActivityId -> prior definitions, newest first. Written on every save. */
  customActivityVersions: Map<string, DemoCustomActivityDefinition[]>;
  maintenanceWindows: DemoMaintenanceWindow[];
  users: UserRow[];
}

type WorldFactory = () => DemoWorld;

let factory: WorldFactory | null = null;
let world: DemoWorld | null = null;
const listeners = new Set<() => void>();

/**
 * Registers how a fresh world is built. Called once at demo start with the seed builder;
 * keeping it injectable lets tests install a small world without importing the seed.
 */
export function configureWorld(build: WorldFactory): void {
  factory = build;
  world = null;
}

export function getWorld(): DemoWorld {
  if (!factory) throw new Error('demo world used before configureWorld()');
  world ??= factory();
  return world;
}

/** Rebuilds the seed world and tells subscribers to refetch. */
export function resetWorld(): void {
  resetRuntimeIds();
  world = null;
  notifyWorld();
}

/** Subscribe to world resets. Returns an unsubscribe function. */
export function subscribeWorld(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function notifyWorld(): void {
  for (const listener of listeners) listener();
}

// --- lookup helpers shared by the handlers ---------------------------------

export function findWorkflow(id: string): Workflow | undefined {
  return getWorld().workflows.find((w) => w.id === id);
}

export function findExecution(id: string): WorkflowExecution | undefined {
  return getWorld().executions.find((e) => e.id === id);
}

export function stepsOf(executionId: string): StepExecution[] {
  return getWorld().steps.get(executionId) ?? [];
}

/**
 * Parsed definition of a workflow. The world stores `definitionJson` because that is what
 * the API returns and what the designer round-trips; callers that need the graph (the run
 * player, the seed derivations) go through here.
 */
export function definitionOf(workflow: Workflow): { nodes: GraphNode[]; edges: GraphEdge[] } {
  try {
    const parsed = JSON.parse(workflow.definitionJson) as { nodes?: GraphNode[]; edges?: GraphEdge[] };
    return { nodes: parsed.nodes ?? [], edges: parsed.edges ?? [] };
  } catch {
    return { nodes: [], edges: [] };
  }
}

/** The subset of a React Flow node the demo backend reasons about. */
export interface GraphNode {
  id: string;
  type?: string;
  position?: { x: number; y: number };
  data?: {
    label?: string;
    activityType?: string;
    targetMachineId?: string | null;
    outputVariable?: string | null;
    disabled?: boolean;
    config?: Record<string, unknown>;
  };
}

/** The subset of a React Flow edge the demo backend reasons about. */
export interface GraphEdge {
  id: string;
  source: string;
  target: string;
  data?: {
    label?: string;
    condition?: string | null;
    disabled?: boolean;
  };
}
