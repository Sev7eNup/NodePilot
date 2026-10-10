import type { AgentTeamIssue, GenerateAgentTeamResponse } from '../api/ai';
import type { AgentDefinition } from '../types/agents';
import type { Credential, MachineOption } from '../types/api';

/** How the user settled one blocking issue: pick a candidate, or drop the request. */
export type IssueResolution = { kind: 'pick'; id: string } | { kind: 'discard' };

export const issueKey = (issue: Pick<AgentTeamIssue, 'memberId' | 'field'>) => `${issue.memberId}:${issue.field}`;

/** Only machine and credential requests can be settled by a pick; the others can only be dropped. */
export const canPick = (issue: AgentTeamIssue): boolean =>
  (issue.field === 'machine' || issue.field === 'credential') && issue.candidates.length > 0;

/** `invalid_config` describes the merged node state, which the user fixes outside this dialog. */
export const isTerminal = (issue: AgentTeamIssue): boolean => issue.severity === 'blocking' && issue.field === 'team';

/** Blocking issues the user still has to settle before the draft can be applied. */
export function openBlockingIssues(issues: AgentTeamIssue[], resolutions: Record<string, IssueResolution>): AgentTeamIssue[] {
  return issues.filter(i => i.severity === 'blocking' && (isTerminal(i) || !resolutions[issueKey(i)]));
}

/** Applies picks to the members; a discard leaves the field empty so the member inherits. */
export function applyResolutions(members: AgentDefinition[], issues: AgentTeamIssue[],
  resolutions: Record<string, IssueResolution>): AgentDefinition[] {
  return members.map(member => {
    let next = member;
    for (const issue of issues) {
      const resolution = resolutions[issueKey(issue)];
      if (issue.memberId !== member.id || resolution?.kind !== 'pick') continue;
      if (issue.field === 'machine') next = { ...next, targetMachineId: resolution.id };
      if (issue.field === 'credential') next = { ...next, credentialId: resolution.id };
    }
    return next;
  });
}

export interface EffectiveBinding {
  machine: { label: string; origin: 'member' | 'step' | 'localhost' };
  credential: { label: string | null; origin: 'member' | 'step' | 'machine' | 'none' };
  serviceIdentity: boolean;
}

/**
 * The target and identity a member really runs with, using the runtime's fallback order:
 * member, then the step, then the machine's default credential; no machine means the
 * NodePilot server. Mirrors `AgentTargetFactory`.
 */
export function effectiveBinding(member: AgentDefinition, step: { machineId?: string | null; credentialId?: string | null },
  machines: MachineOption[], credentials: Credential[], names: Record<string, string>): EffectiveBinding {
  const machineId = member.targetMachineId || step.machineId || null;
  const machine = machineId ? machines.find(m => m.id === machineId) : undefined;
  const machineLabel = machineId ? (machine ? `${machine.name} (${machine.hostname})` : names[machineId] ?? machineId) : '';
  const credentialId = member.credentialId || step.credentialId || machine?.defaultCredentialId || null;
  const credentialOrigin: EffectiveBinding['credential']['origin'] = member.credentialId ? 'member'
    : step.credentialId ? 'step' : machine?.defaultCredentialId ? 'machine' : 'none';
  const credentialLabel = credentialId ? credentials.find(c => c.id === credentialId)?.name ?? names[credentialId] ?? credentialId : null;
  return {
    machine: machineId
      ? { label: machineLabel, origin: member.targetMachineId ? 'member' : 'step' }
      : { label: '', origin: 'localhost' },
    credential: member.useServiceIdentity ? { label: null, origin: 'none' } : { label: credentialLabel, origin: credentialOrigin },
    serviceIdentity: member.useServiceIdentity === true,
  };
}

/** Tools that act on a machine; they inherit the step target when the member names none. */
export const needsTarget = (member: AgentDefinition): boolean =>
  (member.tools ?? []).some(t => /^(files_|logs_collect$|powershell$|cmd$|bash$)/.test(t.name));

/** The config keys the editor writes when the draft is applied. */
export function draftPatch(response: GenerateAgentTeamResponse, members: AgentDefinition[]): Record<string, unknown> {
  const patch: Record<string, unknown> = { members, task: response.patch.task };
  if (response.patch.maxParallelMembers != null) patch.maxParallelMembers = response.patch.maxParallelMembers;
  return patch;
}
