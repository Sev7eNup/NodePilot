import type { Node, Edge } from '@xyflow/react';
import { STATIC_OUTPUT_PARAMETERS_BY_TYPE } from './activityCatalog.generated';
import { isCustomActivityType, getCustomActivityFacts } from './customActivities';

export type VariableType = 'string' | 'number' | 'boolean' | 'object' | 'array' | 'unknown';

export interface UpstreamVariable {
  stepId: string;
  label: string;
  variable: string;
  expression: string;
  type: VariableType;
}

/**
 * Returns the variables exposed by a single node: its main output plus any
 * activity-type-specific params (ManualTrigger parameters, RunScript $variables).
 */
export function describeNodeOutputs(node: Node): UpstreamVariable[] {
  const out: UpstreamVariable[] = [];
  const nodeData = node.data as Record<string, unknown>;
  const varName = (nodeData.outputVariable as string) || node.id;
  const nodeLabel = (nodeData.label as string) || node.id;
  const activityType = (nodeData.activityType as string) || '';

  out.push({
    stepId: node.id,
    label: nodeLabel,
    variable: varName,
    expression: `{{${varName}.output}}`,
    type: 'string'
  });

  if (activityType === 'manualTrigger') {
    addManualTriggerParams(out, node, nodeLabel, varName);
  } else if (activityType === 'webhookTrigger') {
    addWebhookFieldMappingParams(out, node, nodeLabel, varName);
  } else if (activityType === 'returnData') {
    addReturnDataParams(out, node, nodeLabel, varName);
  } else if (activityType === 'runScript') {
    addRunScriptParams(out, node, nodeLabel, varName);
  } else if (activityType === 'registryOperation') {
    addRegistryParams(out, node, nodeLabel, varName);
  } else if (activityType === 'wmiQuery') {
    addWmiCaptureParams(out, node, nodeLabel, varName);
  } else if (isCustomActivityType(activityType)) {
    addCustomActivityParams(out, node.id, nodeLabel, varName, activityType);
  }

  for (const p of STATIC_OUTPUT_PARAMETERS_BY_TYPE[activityType] ?? []) {
    pushParam(out, node.id, nodeLabel, varName, p.name, toVariableType(p.type));
  }

  return out;
}

function addManualTriggerParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const params = (config.parameters as Array<Record<string, unknown>>) || [];
  for (const p of params) {
    const paramName = (p.name as string) || '';
    if (!paramName) continue;
    const paramType = (p.type as string) || 'string';
    pushParam(out, node.id, nodeLabel, varName, paramName, toVariableType(paramType));
  }
}

/**
 * webhookTrigger fieldMappings ([{name, path}]) are user-named JSONPath extractions the
 * controller injects as first-class params — dynamic, so they can't live in the static
 * catalog (which only carries webhookBody/webhookMethod/webhookPath). Mirrored in the MCP
 * VariableResolver.DynamicParams.
 */
function addWebhookFieldMappingParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const mappings = (config.fieldMappings as Array<Record<string, unknown>>) || [];
  if (!Array.isArray(mappings)) return;
  for (const m of mappings) {
    const name = (m?.name as string) || '';
    if (!name) continue;
    pushParam(out, node.id, nodeLabel, varName, name, 'string');
  }
}

function addReturnDataParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const data = (config.data as Record<string, unknown>) || {};
  for (const pName of Object.keys(data)) {
    if (!pName) continue;
    pushParam(out, node.id, nodeLabel, varName, pName, 'unknown');
  }
}

function addRunScriptParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const script = (config.script as string) || '';
  // Note: the always-present `exitCode` param is surfaced via the static activity catalog
  // (STATIC_OUTPUT_PARAMETERS_BY_TYPE), not here — adding it twice would duplicate the picker entry.
  const varMatches = script.matchAll(/\$([a-zA-Z_]\w*)\s*=/g);
  const ignored = new Set([
    'ErrorActionPreference',
    'ProgressPreference',
    'Params',
    '_',
    'null',
    'true',
    'false',
    'input',
    'PSScriptRoot',
    'PSCommandPath'
  ]);
  const seen = new Set<string>(['exitCode']); // reserved — surfaced via the static catalog
  for (const m of varMatches) {
    const pName = m[1];
    if (ignored.has(pName) || seen.has(pName)) continue;
    seen.add(pName);
    pushParam(out, node.id, nodeLabel, varName, pName, 'string', `$${pName}`);
  }
}

function addWmiCaptureParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  // Added 2026-05-17: wmiQuery now projects user-listed CIM properties into param.<Name>
  // (plus an always-present param.count). Surface those to the variable picker so authors
  // get autocomplete on {{wmi_os.param.Caption}} etc. The backend type for a CIM property
  // is dynamic — surface as 'string' which is the dominant case + how everything else is
  // resolved at template-substitution time anyway.
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const captureProperties = config.captureProperties;
  if (Array.isArray(captureProperties)) {
    pushParam(out, node.id, nodeLabel, varName, 'count', 'number');
    for (const entry of captureProperties) {
      if (typeof entry !== 'string') continue;
      const trimmed = entry.trim();
      if (!trimmed || trimmed.toLowerCase() === 'count') continue;
      pushParam(out, node.id, nodeLabel, varName, trimmed, 'string');
    }
  }
}

function addRegistryParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const op = ((config.operation as string) || 'read').toLowerCase();
  const hasValueName = !!(config.valueName as string);
  const params: Array<{ name: string; type: VariableType }> = [];
  if (op === 'read') {
    if (hasValueName) {
      params.push({ name: 'value', type: 'string' }, { name: 'type', type: 'string' });
    } else {
      params.push({ name: 'values', type: 'array' }, { name: 'count', type: 'number' });
    }
  } else if (op === 'listvalues') {
    params.push({ name: 'values', type: 'array' }, { name: 'count', type: 'number' });
  } else if (op === 'listsubkeys') {
    params.push({ name: 'subKeys', type: 'array' }, { name: 'count', type: 'number' });
  } else if (op === 'exists') {
    params.push({ name: 'exists', type: 'boolean' });
  } else if (op === 'createkey') {
    params.push({ name: 'created', type: 'boolean' });
  } else if (op === 'write') {
    params.push({ name: 'type', type: 'string' });
  }
  for (const p of params) {
    pushParam(out, node.id, nodeLabel, varName, p.name, p.type);
  }
}

function addCustomActivityParams(
  out: UpstreamVariable[], stepId: string, nodeLabel: string, varName: string, activityType: string
): void {
  const facts = getCustomActivityFacts(activityType);
  if (!facts) return;
  // Declared outputs + the always-present exitCode (mirrors runScript's static exitCode param).
  pushParam(out, stepId, nodeLabel, varName, 'exitCode', 'number');
  for (const o of facts.outputs) {
    if (o.name === 'exitCode') continue;
    pushParam(out, stepId, nodeLabel, varName, o.name, toVariableType(o.type));
  }
}

function pushParam(
  out: UpstreamVariable[],
  stepId: string,
  nodeLabel: string,
  varName: string,
  paramName: string,
  type: VariableType,
  labelName = paramName
): void {
  out.push({
    stepId,
    label: `${nodeLabel} → ${labelName}`,
    variable: `${varName}.param.${paramName}`,
    expression: `{{${varName}.param.${paramName}}}`,
    type
  });
}

function toVariableType(type: unknown): VariableType {
  return type === 'number' || type === 'boolean' || type === 'object' || type === 'array' || type === 'unknown'
    ? type
    : 'string';
}

/**
 * Walk the graph backwards from the given node to find all upstream nodes
 * and their declared output variables (and exposed parameters for ManualTrigger
 * / RunScript activities). Does NOT include the start node itself.
 */
export function getUpstreamVariables(nodeId: string, allNodes: Node[], edges: Edge[]): UpstreamVariable[] {
  const visited = new Set<string>();
  const queue: string[] = [];
  const result: UpstreamVariable[] = [];

  const incomingByTarget = new Map<string, string[]>();
  for (const edge of edges) {
    const list = incomingByTarget.get(edge.target) || [];
    list.push(edge.source);
    incomingByTarget.set(edge.target, list);
  }

  const directSources = incomingByTarget.get(nodeId) || [];
  queue.push(...directSources);

  while (queue.length > 0) {
    const current = queue.shift()!;
    if (visited.has(current)) continue;
    visited.add(current);

    const node = allNodes.find((n) => n.id === current);
    if (node) {
      result.push(...describeNodeOutputs(node));
    }

    const parents = incomingByTarget.get(current) || [];
    queue.push(...parents);
  }

  return result;
}

/**
 * BFS forward from `producerNodeId` toward `consumerNodeId`, collecting edge IDs
 * along shortest paths. Returns the Set of edge IDs that lie on such a path.
 */
export function findEdgePathBetween(
  producerNodeId: string,
  consumerNodeId: string,
  edges: Edge[]
): Set<string> {
  const adj = new Map<string, Array<{ edgeId: string; target: string }>>();
  for (const e of edges) {
    const list = adj.get(e.source) ?? [];
    list.push({ edgeId: e.id, target: e.target });
    adj.set(e.source, list);
  }

  type Entry = { node: string; path: string[] };
  const queue: Entry[] = [{ node: producerNodeId, path: [] }];
  const visited = new Set<string>([producerNodeId]);
  const result = new Set<string>();

  while (queue.length > 0) {
    const { node, path } = queue.shift()!;
    if (node === consumerNodeId) {
      for (const eid of path) result.add(eid);
      continue;
    }
    for (const { edgeId, target } of adj.get(node) ?? []) {
      if (!visited.has(target)) {
        visited.add(target);
        queue.push({ node: target, path: [...path, edgeId] });
      }
    }
  }
  return result;
}
