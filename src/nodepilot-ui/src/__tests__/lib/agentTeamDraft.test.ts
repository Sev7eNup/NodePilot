import { describe, expect, it } from 'vitest';
import type { AgentTeamIssue } from '../../api/ai';
import {
  applyResolutions, canPick, draftPatch, effectiveBinding, isTerminal, issueKey, needsTarget, openBlockingIssues,
} from '../../lib/agentTeamDraft';
import type { AgentDefinition } from '../../types/agents';
import type { Credential, MachineOption } from '../../types/api';

const member = (patch: Partial<AgentDefinition> = {}): AgentDefinition =>
  ({ id: 'analyst', role: 'Analyst', instructions: '', tools: [], skillIds: [], ...patch });
const issue = (patch: Partial<AgentTeamIssue> = {}): AgentTeamIssue => ({
  severity: 'blocking', memberId: 'analyst', field: 'machine', code: 'unresolved', message: 'm', reference: 'SRV-09',
  candidates: [{ id: 'm1', name: 'SRV-01', detail: 'srv01' }], ...patch,
});
const machine = (id: string, defaultCredentialId: string | null = null): MachineOption => ({
  id, name: `name-${id}`, hostname: `host-${id}`, winRmPort: 5985, useSsl: false, defaultCredentialId,
  tags: null, lastConnectivityCheck: null, isReachable: true,
});
const credential = (id: string): Credential => ({ id, name: `cred-${id}`, username: 'u', domain: null, expiresAt: null });

describe('openBlockingIssues', () => {
  it('keeps blocking issues until they are resolved and ignores warnings', () => {
    const issues = [issue(), issue({ severity: 'warning', field: 'tool' })];
    expect(openBlockingIssues(issues, {})).toHaveLength(1);
    expect(openBlockingIssues(issues, { [issueKey(issues[0])]: { kind: 'discard' } })).toHaveLength(0);
  });

  it('never closes a team-level issue', () => {
    const terminal = issue({ field: 'team', memberId: '' });
    expect(isTerminal(terminal)).toBe(true);
    expect(openBlockingIssues([terminal], { [issueKey(terminal)]: { kind: 'discard' } })).toHaveLength(1);
  });

  it('allows a pick only for machine and credential with candidates', () => {
    expect(canPick(issue())).toBe(true);
    expect(canPick(issue({ candidates: [] }))).toBe(false);
    expect(canPick(issue({ field: 'serviceIdentity' }))).toBe(false);
  });
});

describe('applyResolutions', () => {
  it('writes a picked machine and credential onto the right member only', () => {
    const members = [member(), member({ id: 'other' })];
    const issues = [issue(), issue({ field: 'credential', candidates: [{ id: 'c1', name: 'svc', detail: null }] })];
    const result = applyResolutions(members, issues, {
      [issueKey(issues[0])]: { kind: 'pick', id: 'm1' }, [issueKey(issues[1])]: { kind: 'pick', id: 'c1' },
    });
    expect(result[0]).toMatchObject({ targetMachineId: 'm1', credentialId: 'c1' });
    expect(result[1].targetMachineId).toBeUndefined();
  });

  it('leaves the field empty on discard', () => {
    const issues = [issue()];
    const result = applyResolutions([member()], issues, { [issueKey(issues[0])]: { kind: 'discard' } });
    expect(result[0].targetMachineId).toBeUndefined();
  });
});

describe('effectiveBinding', () => {
  const machines = [machine('a', 'c-default'), machine('b')];
  const credentials = [credential('c-default'), credential('c-step'), credential('c-own')];

  it('uses the member machine and credential first', () => {
    const b = effectiveBinding(member({ targetMachineId: 'b', credentialId: 'c-own' }), { machineId: 'a', credentialId: 'c-step' }, machines, credentials, {});
    expect(b.machine).toEqual({ label: 'name-b (host-b)', origin: 'member' });
    expect(b.credential).toEqual({ label: 'cred-c-own', origin: 'member' });
  });

  it('falls back to the step target, then to the machine default credential', () => {
    const b = effectiveBinding(member(), { machineId: 'a' }, machines, credentials, {});
    expect(b.machine.origin).toBe('step');
    expect(b.credential).toEqual({ label: 'cred-c-default', origin: 'machine' });
  });

  it('prefers the step credential over the machine default', () => {
    const b = effectiveBinding(member(), { machineId: 'a', credentialId: 'c-step' }, machines, credentials, {});
    expect(b.credential).toEqual({ label: 'cred-c-step', origin: 'step' });
  });

  it('reports the NodePilot server and no credential when nothing is set', () => {
    const b = effectiveBinding(member(), {}, machines, credentials, {});
    expect(b.machine.origin).toBe('localhost');
    expect(b.credential).toEqual({ label: null, origin: 'none' });
  });

  it('marks the service identity and ignores credentials then', () => {
    const b = effectiveBinding(member({ useServiceIdentity: true, credentialId: 'c-own' }), {}, machines, credentials, {});
    expect(b.serviceIdentity).toBe(true);
    expect(b.credential.label).toBeNull();
  });

  it('falls back to the server-provided name for an unknown id', () => {
    const b = effectiveBinding(member({ targetMachineId: 'zz' }), {}, machines, credentials, { zz: 'Remote-ZZ' });
    expect(b.machine.label).toBe('Remote-ZZ');
  });
});

describe('needsTarget and draftPatch', () => {
  it('detects tools that act on a machine', () => {
    expect(needsTarget(member({ tools: [{ name: 'files_read' }] }))).toBe(true);
    expect(needsTarget(member({ tools: [{ name: 'powershell' }] }))).toBe(true);
    expect(needsTarget(member({ tools: [{ name: 'http_request' }, { name: 'workflow_run' }] }))).toBe(false);
  });

  it('writes the validated task and only sets the parallel limit when present', () => {
    const response = { patch: { members: [], task: 'Do it', maxParallelMembers: null }, names: {}, issues: [], retried: false, durationMs: 1, model: 'm' };
    expect(draftPatch(response, [member()])).toEqual({ members: [member()], task: 'Do it' });
    expect(draftPatch({ ...response, patch: { ...response.patch, maxParallelMembers: 2 } }, [])).toMatchObject({ maxParallelMembers: 2 });
  });
});
