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

const NOTES: Record<'de' | 'en', Record<string, string>> = {
  de: {
    note_probe: '1. Freien Speicher simulieren\nIm Startdialog gibst du freeSpaceGb ein. Die Demo übernimmt diesen Wert für die Speicherplatzprüfung; sie liest kein echtes Laufwerk.',
    note_branch: '2. Einen Zweig auswählen\nUnter 10 GB: critical. Unter 25 GB: warning. Sonst: healthy. Junction: waitAny führt den gewählten Zweig wieder zum Ergebnis zusammen.',
    note_input: '1. Ordner und Alter wählen\npath ist der Quellordner, olderThanDays die Altersgrenze in Tagen. Lass für diese Übung beide Werte unverändert. Es werden keine echten Dateien verändert.',
    note_finish: '2. Parallel arbeiten und zusammenführen\nPrüfsumme und Bereinigung laufen gleichzeitig. Junction: waitAll wartet auf beide, bevor die Abschlussmail startet.',
    note_check: '1. Dienstzustand simulieren\nStarte manuell mit serviceState = Stopped. Die Entscheidung wählt den Wiederherstellungszweig. Diese Übung hat keinen Zeitplan.',
    note_recover: '2. Starten und erneut prüfen\nNach dem Start wartet der Workflow kurz und liest den Status erneut. Running führt zur Erfolgsmeldung; ein anderer Status zur Fehlermeldung.',
  },
  en: {
    note_probe: '1. Simulate free space\nEnter freeSpaceGb in the start dialog. The demo uses that value for the disk-space check; it does not read a real drive.',
    note_branch: '2. Select one branch\nBelow 10 GB: critical. Below 25 GB: warning. Otherwise: healthy. Junction: waitAny joins the selected branch back into the result.',
    note_input: '1. Choose folder and age\npath is the source folder; olderThanDays is the age threshold in days. Leave both unchanged for this exercise. No real files are modified.',
    note_finish: '2. Work in parallel, then join\nChecksum and cleanup run together. Junction: waitAll waits for both before starting the summary email.',
    note_check: '1. Simulate a service state\nStart manually with serviceState = Stopped. The decision selects recovery. This exercise does not run on a schedule.',
    note_recover: '2. Start and verify\nAfter starting the service, wait briefly and read its state again. Running leads to confirmation; another state leads to a failure notification.',
  },
};

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

function fixtureDefinition(id: keyof typeof MISSION_WORKFLOW_IDS, language: 'de' | 'en'): { nodes: GraphNode[]; edges: GraphEdge[] } | null {
  if (id === 'build') return { nodes: buildNodes(), edges: [] };
  if (id === 'live') return { nodes: liveNodes(), edges: [
    { id: 'live-trigger-wait', source: 'trigger', target: 'wait', data: { label: 'Always' } },
    { id: 'live-wait-result', source: 'wait', target: 'result', data: { label: 'On Success' } },
  ] };
  const source = getWorld().workflows.find(workflow => workflow.id === sourceIds[id]);
  if (!source) return null;
  const definition = structuredClone(definitionOf(source));
  for (const node of definition.nodes) {
    if (node.data?.activityType === 'note' && NOTES[language][node.id]) Object.assign(node.data, { text: NOTES[language][node.id] });
  }
  if (id === 'decision' || id === 'service') {
    const trigger = definition.nodes.find(node => node.id === 'trg');
    if (!trigger?.data) return null;
    trigger.data = { ...trigger.data, label: id === 'decision' ? 'Manual Trigger: free space' : 'Manual Trigger: service state', activityType: 'manualTrigger', config: { parameters: [{ name: id === 'decision' ? 'freeSpaceGb' : 'serviceState', type: 'string', required: true, default: id === 'decision' ? '8' : 'Stopped' }] } };
  }
  return definition;
}

/** Creates one fresh, local copy when a task starts; re-entry never overwrites visitor edits. */
export function ensureMissionWorkflow(id: keyof typeof MISSION_WORKFLOW_IDS, language: 'de' | 'en' = 'en'): Workflow | null {
  const world = getWorld();
  const workflowId = MISSION_WORKFLOW_IDS[id];
  const existing = world.workflows.find(workflow => workflow.id === workflowId);
  if (existing) return existing;
  const definition = fixtureDefinition(id, language);
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
