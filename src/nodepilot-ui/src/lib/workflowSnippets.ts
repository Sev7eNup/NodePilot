import type { Node, Edge } from '@xyflow/react';
import i18n from '../i18n';

/**
 * A workflow snippet is a predefined mini-pattern the user drops onto the canvas with one
 * click. Saves re-building common constructs by hand every time (try/catch, a for-each
 * loop, fan-out + join).
 *
 * The snippet definition describes nodes + edges using the same workflow-JSON syntax that
 * ends up in DefinitionJson. IDs are regenerated on insert so pasting the same snippet
 * more than once never causes an id clash.
 */
export interface WorkflowSnippet {
  id: string;
  name: string;
  description: string;
  icon: string;
  nodes: Array<{
    localId: string; // replaced with a fresh UUID on insert; edges reference each other by this id
    dx: number;      // x offset relative to the cursor/insert origin
    dy: number;
    label: string;
    activityType: string;
    config?: Record<string, unknown>;
    outputVariable?: string;
  }>;
  edges: Array<{
    fromLocalId: string;
    toLocalId: string;
    label?: string;
    condition?: string;
  }>;
}

/**
 * Built fresh on each call so the snippet `name`/`description` strings resolve against the
 * current language (and so i18n is initialized by the time they're read). The NodeLibrary
 * snippet picker calls this at render time — never cache the result across a language switch.
 */
export function getWorkflowSnippets(): WorkflowSnippet[] {
  return [
  {
    id: 'foreach-pattern',
    name: i18n.t('designer:snippets.foreach.name'),
    description: i18n.t('designer:snippets.foreach.description'),
    icon: 'loop',
    nodes: [
      { localId: 'produce', dx: 0,   dy: 0, label: 'Produce list', activityType: 'runScript',
        config: { script: "# return comma-separated items\nWrite-Output 'host1,host2,host3'" },
        outputVariable: 'list' },
      { localId: 'foreach', dx: 260, dy: 0, label: 'For each item', activityType: 'forEach',
        config: { items: '{{list.output}}', separator: ',', childWorkflowNameOrId: '' } },
      { localId: 'done',    dx: 520, dy: 0, label: 'All items done', activityType: 'log',
        config: { level: 'info', message: 'Processed all items' } },
    ],
    edges: [
      { fromLocalId: 'produce', toLocalId: 'foreach' },
      { fromLocalId: 'foreach', toLocalId: 'done', condition: 'foreach.success' },
    ],
  },
  {
    id: 'http-retry',
    name: i18n.t('designer:snippets.httpRetry.name'),
    description: i18n.t('designer:snippets.httpRetry.description'),
    icon: 'language',
    nodes: [
      { localId: 'http',     dx: 0,   dy: 0,   label: 'HTTP call', activityType: 'restApi',
        config: {
          url: 'https://api.example.com/resource',
          method: 'GET',
          timeoutSeconds: 15,
          retry: { maxAttempts: 3, backoff: 'exponential', initialDelayMs: 1000, maxDelayMs: 10000 },
        },
        outputVariable: 'api' },
      { localId: 'fail-log', dx: 260, dy: 150, label: 'Alert ops',  activityType: 'log',
        config: { level: 'error', message: 'API call failed after retries: {{api.error}}' } },
      { localId: 'success',  dx: 260, dy: -150, label: 'Process response', activityType: 'runScript',
        config: { script: "# use {{api.output}}\nWrite-Output 'processed'" } },
    ],
    edges: [
      { fromLocalId: 'http', toLocalId: 'success', label: 'On Success', condition: 'http.success' },
      { fromLocalId: 'http', toLocalId: 'fail-log', label: 'On Failure', condition: 'http.failed' },
    ],
  },
  {
    id: 'parallel-fanout',
    name: i18n.t('designer:snippets.parallelFanout.name'),
    description: i18n.t('designer:snippets.parallelFanout.description'),
    icon: 'call_split',
    nodes: [
      { localId: 'split',    dx: 0,   dy: 0,   label: 'Fan-out trigger', activityType: 'log',
        config: { level: 'info', message: 'Starting parallel work' } },
      { localId: 'branch-a', dx: 260, dy: -150, label: 'Branch A',       activityType: 'runScript',
        config: { script: "Write-Output 'A'" }, outputVariable: 'a' },
      { localId: 'branch-b', dx: 260, dy: 0,    label: 'Branch B',       activityType: 'runScript',
        config: { script: "Write-Output 'B'" }, outputVariable: 'b' },
      { localId: 'branch-c', dx: 260, dy: 150,  label: 'Branch C',       activityType: 'runScript',
        config: { script: "Write-Output 'C'" }, outputVariable: 'c' },
      { localId: 'join',     dx: 520, dy: 0,    label: 'Wait all',       activityType: 'junction',
        config: { mode: 'waitAll' } },
      { localId: 'after',    dx: 780, dy: 0,    label: 'After join',     activityType: 'log',
        config: { level: 'info', message: 'A={{a.output}} B={{b.output}} C={{c.output}}' } },
    ],
    edges: [
      { fromLocalId: 'split',    toLocalId: 'branch-a' },
      { fromLocalId: 'split',    toLocalId: 'branch-b' },
      { fromLocalId: 'split',    toLocalId: 'branch-c' },
      { fromLocalId: 'branch-a', toLocalId: 'join' },
      { fromLocalId: 'branch-b', toLocalId: 'join' },
      { fromLocalId: 'branch-c', toLocalId: 'join' },
      { fromLocalId: 'join',     toLocalId: 'after' },
    ],
  },
  {
    id: 'try-catch-script',
    name: i18n.t('designer:snippets.tryCatch.name'),
    description: i18n.t('designer:snippets.tryCatch.description'),
    icon: 'shield',
    nodes: [
      { localId: 'try',     dx: 0,   dy: 0,   label: 'Try script',       activityType: 'runScript',
        config: { engine: 'auto', timeoutSeconds: 60, script: "# your script here\nWrite-Output 'hello'" } },
      { localId: 'catch',   dx: 260, dy: 150, label: 'On failure — log', activityType: 'log',
        config: { level: 'error', message: 'Script failed: {{try.error}}' } },
      { localId: 'continue',dx: 520, dy: 0,   label: 'Continue',         activityType: 'log',
        config: { level: 'info', message: 'Post-script handler' } },
    ],
    edges: [
      { fromLocalId: 'try',   toLocalId: 'continue', label: 'On Success', condition: 'try.success' },
      { fromLocalId: 'try',   toLocalId: 'catch',    label: 'On Failure', condition: 'try.failed' },
      { fromLocalId: 'catch', toLocalId: 'continue', label: 'Always' },
    ],
  },
  ];
}

/**
 * Eager snapshot for consumers that only need the stable structure (ids, nodes, edges) —
 * e.g. `insertSnippet` lookups by id. The localized `name`/`description` are resolved against
 * whatever language is active at module-load time; UI surfaces that must react to a language
 * switch (the NodeLibrary picker) call {@link getWorkflowSnippets} at render time instead.
 */
export const WORKFLOW_SNIPPETS: WorkflowSnippet[] = getWorkflowSnippets();

/**
 * Inserts a snippet into an existing workflow at the given origin (flow-space coordinates).
 * Returns the new nodes + edges arrays that the caller will pass to setNodes/setEdges.
 * Generates fresh crypto-UUID-suffix IDs so multiple inserts of the same snippet don't clash.
 */
export function insertSnippet(
  snippet: WorkflowSnippet,
  origin: { x: number; y: number },
  existingNodes: Node[],
  existingEdges: Edge[],
): { nodes: Node[]; edges: Edge[]; newNodeIds: string[] } {
  const idSuffix = crypto.randomUUID().slice(0, 6);
  const idMap = new Map<string, string>();
  for (const n of snippet.nodes) {
    idMap.set(n.localId, `${n.localId}-${idSuffix}`);
  }
  const newNodes: Node[] = snippet.nodes.map((n) => ({
    id: idMap.get(n.localId)!,
    type: 'activity',
    position: { x: origin.x + n.dx, y: origin.y + n.dy },
    selected: true,
    data: {
      label: n.label,
      activityType: n.activityType,
      config: n.config ?? {},
      ...(n.outputVariable ? { outputVariable: n.outputVariable } : {}),
      targetMachineId: null,
      credentialId: null,
    },
  }));
  const newEdges: Edge[] = snippet.edges.map((e) => ({
    id: `edge-${idSuffix}-${e.fromLocalId}-${e.toLocalId}`,
    source: idMap.get(e.fromLocalId)!,
    target: idMap.get(e.toLocalId)!,
    type: 'labeled',
    selected: true,
    data: {
      label: e.label ?? '',
      condition: e.condition ?? '',
      disabled: false,
    },
  }));

  return {
    nodes: [...existingNodes.map((n) => ({ ...n, selected: false })), ...newNodes],
    edges: [...existingEdges.map((e) => ({ ...e, selected: false })), ...newEdges],
    newNodeIds: newNodes.map((n) => n.id),
  };
}
