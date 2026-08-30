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

/**
 * Mirror of NodePilot.Core.Activities.PowerShellReservedVariables. A runScript step never
 * publishes these: the wrapper withholds them so an upstream value cannot shadow PowerShell's
 * own state, so offering them in the picker would advertise a variable that never resolves.
 * PowerShellReservedVariablesParityTests keeps this list in step with the backend.
 */
export const RESERVED_POWERSHELL_VARIABLES = [
  // Automatic variables.
  '_', 'PSItem', 'args', 'input', 'this', 'foreach', 'switch', 'Matches',
  'Error', 'LASTEXITCODE', 'StackTrace', 'MyInvocation', 'PSBoundParameters',
  'PSCmdlet', 'PSCommandPath', 'PSScriptRoot', 'PSVersionTable', 'PID', 'HOME', 'PWD',
  'ExecutionContext', 'Host', 'ShellId', 'ConsoleFileName', 'PSCulture', 'PSUICulture',
  'PSEdition', 'PSHOME', 'NestedPromptLevel', 'OutputEncoding', 'PSStyle',
  'true', 'false', 'null',
  // Preference variables.
  'ErrorActionPreference', 'WarningPreference', 'VerbosePreference', 'DebugPreference',
  'InformationPreference', 'ProgressPreference', 'ConfirmPreference', 'WhatIfPreference',
  'ErrorView', 'MaximumErrorCount', 'MaximumAliasCount', 'MaximumDriveCount',
  'MaximumFunctionCount', 'MaximumVariableCount', 'PSDefaultParameterValues',
  'PSModuleAutoLoadingPreference', 'PSNativeCommandUseErrorActionPreference',
  'PSNativeCommandArgumentPassing', 'PSEmailServer', 'PSSessionApplicationName',
  'PSSessionConfigurationName', 'PSSessionOption', 'Transcript'
] as const;

/** Prefix the script wrapper reserves for its own variables. */
export const RESERVED_POWERSHELL_PREFIX = '__np';

const reservedLookup = new Set<string>(
  RESERVED_POWERSHELL_VARIABLES.map((n) => n.toLowerCase()).concat(['params'])
);

function isReservedPowerShellVariable(name: string): boolean {
  const lower = name.toLowerCase();
  return reservedLookup.has(lower) || lower.startsWith(RESERVED_POWERSHELL_PREFIX);
}

function addRunScriptParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  const nodeData = node.data as Record<string, unknown>;
  const config = (nodeData.config as Record<string, unknown>) || {};
  const script = (config.script as string) || '';
  // The always-present `exitCode` param comes from the static activity catalog
  // (STATIC_OUTPUT_PARAMETERS_BY_TYPE); adding it here too would duplicate the picker entry.
  const varMatches = script.matchAll(/\$([a-zA-Z_]\w*)\s*=/g);
  const seen = new Set<string>(['exitCode']); // reserved: comes from the static catalog
  for (const m of varMatches) {
    const pName = m[1];
    if (isReservedPowerShellVariable(pName) || seen.has(pName)) continue;
    seen.add(pName);
    pushParam(out, node.id, nodeLabel, varName, pName, 'string', `$${pName}`);
  }
}

function addWmiCaptureParams(out: UpstreamVariable[], node: Node, nodeLabel: string, varName: string): void {
  // wmiQuery projects the user-listed CIM properties into param.<Name>, plus an always-present
  // param.count. Surface them to the variable picker so authors get autocomplete on
  // {{wmi_os.param.Caption}}. A CIM property has no fixed backend type, so report 'string',
  // which is the dominant case and matches how values are substituted at run time.
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
 * Walks the graph backwards from the given node to collect all upstream nodes and their
 * declared output variables, plus the parameters exposed by ManualTrigger and RunScript
 * activities. The start node itself is not included.
 */
/**
 * The param names a node publishes because of how it is configured — a script's assignments, a
 * returnData's keys, a wmiQuery's captured properties.
 *
 * Excludes the static catalog outputs every instance of a type emits: two runScript steps both
 * publish `exitCode`, which collides by construction and is not an authoring problem. Mirror of
 * NodePilot.Core WorkflowDataBusAnalyzer.AuthoredParameters; used by the canvas linter to flag
 * two activities claiming the same name.
 */
export function authoredParamNames(node: Node): string[] {
  const out: UpstreamVariable[] = [];
  const nodeData = node.data as Record<string, unknown>;
  const varName = (nodeData.outputVariable as string) || node.id;
  const nodeLabel = (nodeData.label as string) || node.id;
  const activityType = (nodeData.activityType as string) || '';

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
  }

  const prefix = `{{${varName}.param.`;
  return out
    .filter((v) => v.expression.startsWith(prefix))
    .map((v) => v.expression.slice(prefix.length, -2));
}

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
