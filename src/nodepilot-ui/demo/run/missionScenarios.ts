import { MISSION_WORKFLOW_IDS } from '../seed/missionFixtures';
import type { GraphEdge, GraphNode } from '../state/world';
import type { StepOutcome } from './outcomes';

export type MissionOutcome = StepOutcome & { error?: string };

/** Only guided copies use these plans. Ordinary demo workflows keep their existing walk. */
export function missionPlan(workflowId: string, nodes: GraphNode[], edges: GraphEdge[], inputs: Record<string, string>): string[][] | null {
  let groups: string[][] | null = null;
  if (workflowId === MISSION_WORKFLOW_IDS.decision) {
    const free = Number(inputs.freeSpaceGb ?? '8');
    groups = free < 10
      ? [['trg'], ['probe'], ['dec'], ['top_dirs'], ['mail_critical'], ['gather'], ['ret']]
      : free < 25
        ? [['trg'], ['probe'], ['dec'], ['log_warning'], ['gather'], ['ret']]
        : [['trg'], ['probe'], ['dec'], ['log_ok'], ['gather'], ['ret']];
  } else if (workflowId === MISSION_WORKFLOW_IDS.parallel) {
    groups = [['trg'], ['inventory'], ['archive'], ['checksum', 'purge'], ['gather'], ['mail'], ['ret']];
  } else if (workflowId === MISSION_WORKFLOW_IDS.service) {
    groups = (inputs.serviceState ?? 'Stopped').toLowerCase() === 'running'
      ? [['trg'], ['status'], ['dec'], ['log_ok'], ['gather'], ['ret']]
      : [['trg'], ['status'], ['dec'], ['start'], ['settle'], ['verify'], ['mail_recovered'], ['gather'], ['ret']];
  }
  if (!groups) return null;
  const available = new Set(nodes.filter(node => !node.data?.disabled).map(node => node.id));
  const connections = new Set(edges.filter(edge => !edge.data?.disabled).map(edge => `${edge.source}->${edge.target}`));
  const required = workflowId === MISSION_WORKFLOW_IDS.decision
    ? ['trg->probe', 'probe->dec', 'dec->top_dirs', 'top_dirs->mail_critical', 'mail_critical->gather', 'log_warning->gather', 'log_ok->gather', 'gather->ret']
    : workflowId === MISSION_WORKFLOW_IDS.parallel
      ? ['trg->inventory', 'inventory->archive', 'archive->checksum', 'archive->purge', 'checksum->gather', 'purge->gather', 'gather->mail', 'mail->ret']
      : ['trg->status', 'status->dec', 'dec->start', 'start->settle', 'settle->verify', 'verify->mail_recovered', 'mail_recovered->gather', 'gather->ret'];
  return groups.every(group => group.every(id => available.has(id))) && required.every(connection => connections.has(connection)) ? groups : null;
}

function result(output: string, outputParameters: Record<string, string> = {}, durationMs = 600): MissionOutcome {
  return { output, outputParameters, durationMs };
}

export function missionOutcome(workflowId: string, node: GraphNode, inputs: Record<string, string>): MissionOutcome | null {
  const id = node.id;
  if (workflowId === MISSION_WORKFLOW_IDS.build) {
    if (id === 'trigger') return result('Started manually.');
    if (id === 'script') return result('Hello from NodePilot', { message: 'Hello from NodePilot', exitCode: '0' });
    if (id === 'result') return result('{"message":"Hello from NodePilot"}', { message: 'Hello from NodePilot' });
  }
  if (workflowId === MISSION_WORKFLOW_IDS.decision) {
    const free = Number(inputs.freeSpaceGb ?? '8');
    const severity = free < 10 ? 'critical' : free < 25 ? 'warning' : 'healthy';
    if (id === 'trg') return result(`Manual probe: ${free} GB free.`, { freeSpaceGb: String(free) });
    if (id === 'probe') return result(`C: has ${free} GB free.`, { freeSpaceGb: String(free) });
    if (id === 'dec') return result(`Branch: ${severity}`, { branch: severity });
    if (id === 'top_dirs') return result('Collected the ten largest folders.', { folderCount: '10' });
    if (id === 'mail_critical') return result('Critical disk-space alert queued.');
    if (id === 'log_warning') return result('Disk-space warning logged.');
    if (id === 'log_ok') return result('Healthy disk-space entry logged.');
    if (id === 'gather') return result(`The ${severity} branch completed.`);
    if (id === 'ret') return result(JSON.stringify({ freeSpaceGb: free, severity }), { freeSpaceGb: String(free), severity });
  }
  if (workflowId === MISSION_WORKFLOW_IDS.parallel) {
    if (id === 'checksum') return result('Archive checksum verified.', { hash: '9f2c4b1e' }, 3200);
    if (id === 'purge') return result('Archived originals removed.', { removed: '124' }, 3600);
    if (id === 'gather') return result('Both parallel branches completed.');
    if (id === 'mail') return result('Cleanup summary mail queued.');
    if (id === 'ret') return result('Archive and checksum returned.', { archive: 'reports-2026-09.zip', verified: 'true' });
  }
  if (workflowId === MISSION_WORKFLOW_IDS.service) {
    const stopped = (inputs.serviceState ?? 'Stopped').toLowerCase() !== 'running';
    if (id === 'trg') return result(`Initial service state: ${stopped ? 'Stopped' : 'Running'}.`, { serviceState: stopped ? 'Stopped' : 'Running' });
    if (id === 'status') return result(`Print Spooler is ${stopped ? 'Stopped' : 'Running'}.`, { status: stopped ? 'Stopped' : 'Running' });
    if (id === 'dec') return result(`Branch: ${stopped ? 'stopped' : 'running'}.`, { branch: stopped ? 'stopped' : 'running' });
    if (id === 'start') return result('Print Spooler start requested.', { status: 'StartPending' });
    if (id === 'settle') return result('Waited for the service to settle.', {}, 2400);
    if (id === 'verify') return result('Print Spooler is Running.', { status: 'Running' });
    if (id === 'mail_recovered') return result('Recovery confirmation queued.');
    if (id === 'log_ok') return result('Service was already running.');
    if (id === 'gather') return result('The selected service-state branch completed.');
    if (id === 'ret') return result('{"service":"Spooler","status":"Running"}', { service: 'Spooler', status: 'Running' });
  }
  if (workflowId === MISSION_WORKFLOW_IDS.live) {
    if (id === 'trigger') return result('Started manually.');
    if (id === 'wait') return result('This simulated check waits for cancellation.');
    if (id === 'result') return result('{"status":"complete"}', { status: 'complete' });
  }
  return null;
}
