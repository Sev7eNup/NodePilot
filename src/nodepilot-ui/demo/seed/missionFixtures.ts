/** Isolated workflow copies for guided tasks. They never enter the product build. */
import type { Workflow } from '../../src/types/api';
import { DEMO_USER, machineIds } from './entities';
import { demoId } from '../state/ids';
import { definitionOf, getWorld, notifyWorld, type GraphEdge, type GraphNode } from '../state/world';

export type NewMissionId = 'build' | 'decision' | 'parallel' | 'service' | 'live' | 'versions' | 'machine' | 'maintenance';
export const MISSION_WORKFLOW_IDS = {
  build: demoId('workflow:tour-build'),
  decision: demoId('workflow:tour-decision'),
  parallel: demoId('workflow:tour-parallel'),
  service: demoId('workflow:tour-service'),
  live: demoId('workflow:tour-live'),
} as const;

const sourceIds = {
  decision: demoId('workflow:disk-space-watch'),
  parallel: demoId('workflow:temp-file-cleanup'),
  service: demoId('workflow:service-recovery'),
} as const;

function buildNodes(): GraphNode[] {
  return [
    { id: 'trigger', type: 'activity', position: { x: 0, y: 200 }, data: { label: 'Manual Trigger: hello', activityType: 'manualTrigger', targetMachineId: null, config: { parameters: [] } } },
    { id: 'script', type: 'activity', position: { x: 370, y: 200 }, data: { label: 'PowerShell: greeting', activityType: 'runScript', targetMachineId: machineIds()[0], config: { script: "$message = 'Hello from NodePilot'" } } },
    { id: 'result', type: 'activity', position: { x: 740, y: 200 }, data: { label: 'Return Data: greeting', activityType: 'returnData', targetMachineId: null, config: { data: { message: '{{script.param.message}}' } } } },
  ];
}

function liveNodes(): GraphNode[] {
  return [
    { id: 'trigger', type: 'activity', position: { x: 0, y: 200 }, data: { label: 'Manual Trigger: live check', activityType: 'manualTrigger', targetMachineId: null, config: { parameters: [] } } },
    { id: 'wait', type: 'activity', position: { x: 370, y: 200 }, data: { label: 'Wait for condition: long check', activityType: 'waitForCondition', targetMachineId: machineIds()[0], config: { condition: '$true', timeoutSeconds: 30, pollIntervalSeconds: 5 } } },
    { id: 'result', type: 'activity', position: { x: 740, y: 200 }, data: { label: 'Return Data: check', activityType: 'returnData', targetMachineId: null, config: { data: { status: 'complete' } } } },
  ];
}

function fixtureDefinition(id: keyof typeof MISSION_WORKFLOW_IDS): { nodes: GraphNode[]; edges: GraphEdge[] } | null {
  if (id === 'build') return { nodes: buildNodes(), edges: [] };
  if (id === 'live') return { nodes: liveNodes(), edges: [
    { id: 'live-trigger-wait', source: 'trigger', target: 'wait', data: { label: 'Always' } },
    { id: 'live-wait-result', source: 'wait', target: 'result', data: { label: 'On Success' } },
  ] };
  const source = getWorld().workflows.find(workflow => workflow.id === sourceIds[id]);
  if (!source) return null;
  const definition = structuredClone(definitionOf(source));
  if (id === 'decision' || id === 'service') {
    const trigger = definition.nodes.find(node => node.id === 'trg');
    if (!trigger?.data) return null;
    trigger.data = { ...trigger.data, label: id === 'decision' ? 'Manual Trigger: free space' : 'Manual Trigger: service state', activityType: 'manualTrigger', config: { parameters: [{ name: id === 'decision' ? 'freeSpaceGb' : 'serviceState', type: 'string', required: true, default: id === 'decision' ? '8' : 'Stopped' }] } };
  }
  return definition;
}

/** Creates one fresh, local copy when a task starts; re-entry never overwrites visitor edits. */
export function ensureMissionWorkflow(id: keyof typeof MISSION_WORKFLOW_IDS): Workflow | null {
  const world = getWorld();
  const workflowId = MISSION_WORKFLOW_IDS[id];
  const existing = world.workflows.find(workflow => workflow.id === workflowId);
  if (existing) return existing;
  const definition = fixtureDefinition(id);
  if (!definition) return null;
  const source = id === 'decision' || id === 'parallel' || id === 'service'
    ? world.workflows.find(workflow => workflow.id === sourceIds[id]) : world.workflows[0];
  if (!source) return null;
  const now = new Date().toISOString();
  const name = { build: 'Your First Workflow', decision: 'Disk Space Watch · Guided', parallel: 'Temp File Cleanup · Guided', service: 'Service Recovery Watchdog · Guided', live: 'Live Ops Check' }[id];
  const workflow: Workflow = {
    ...source, id: workflowId, name, description: 'Browser-demo walkthrough fixture.',
    definitionJson: JSON.stringify(definition), version: 1, activityCount: definition.nodes.filter(node => node.data?.activityType !== 'note').length,
    triggerTypes: ['manualTrigger'], isEnabled: id !== 'build', createdAt: now, updatedAt: now,
    createdBy: DEMO_USER.username, updatedBy: DEMO_USER.username,
    checkedOutByUserId: id === 'build' ? DEMO_USER.id : null,
    checkedOutByUserName: id === 'build' ? DEMO_USER.username : null,
    checkedOutAt: id === 'build' ? now : null,
    lastExecution: null, successCount: 0, totalCount: 0, avgDurationMs: null,
  };
  world.workflows.unshift(workflow);
  world.versions.set(workflowId, []);
  for (const folder of world.folders) folder.workflowCount = world.workflows.filter(item => item.folderId === folder.id).length;
  for (const machine of world.machines) machine.usedByWorkflowCount = world.workflows.filter(item => item.definitionJson.includes(machine.id)).length;
  notifyWorld();
  return workflow;
}
