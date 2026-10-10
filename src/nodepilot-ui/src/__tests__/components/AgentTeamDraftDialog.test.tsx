import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AgentTeamDraftDialog } from '../../components/designer/agents/AgentTeamDraftDialog';
import { aiApi, type GenerateAgentTeamResponse } from '../../api/ai';
import type { MachineOption } from '../../types/api';

const machines: MachineOption[] = [{
  id: 'm1', name: 'SRV-01', hostname: 'srv01', winRmPort: 5985, useSsl: false, defaultCredentialId: null,
  tags: null, lastConnectivityCheck: null, isReachable: true,
}];

const response = (patch: Partial<GenerateAgentTeamResponse> = {}): GenerateAgentTeamResponse => ({
  patch: {
    task: 'Diagnose the service',
    maxParallelMembers: 1,
    members: [
      { id: 'lead', role: 'Lead', instructions: 'Coordinate.', isSupervisor: true, tools: [], skillIds: [] },
      {
        id: 'analyst', role: 'Analyst', instructions: 'Read the logs.', targetMachineId: 'm1', tools: [
          { name: 'files_read', allowedPaths: ['C:\\Logs'] },
          { name: 'http_request', allowedHosts: [] },
        ], skillIds: [],
      },
    ],
  },
  names: { m1: 'SRV-01' }, issues: [], retried: false, durationMs: 42, model: 'test-model', ...patch,
});

function setup(config: Record<string, unknown> = {}) {
  const onApply = vi.fn();
  const onClose = vi.fn();
  render(<AgentTeamDraftDialog config={config} step={{}} machines={machines} credentials={[]} onApply={onApply} onClose={onClose} />);
  return { onApply, onClose };
}

async function generate() {
  fireEvent.change(screen.getByLabelText('Team description'), { target: { value: 'Analyst reads C:\\Logs on SRV-01' } });
  fireEvent.click(screen.getByRole('button', { name: 'Draft team' }));
  await screen.findByText('Review team draft');
}

describe('AgentTeamDraftDialog', () => {
  let generateSpy: ReturnType<typeof vi.spyOn>;
  beforeEach(() => { generateSpy = vi.spyOn(aiApi, 'generateAgentTeam'); });
  afterEach(() => vi.restoreAllMocks());

  it('sends the description with the current config', async () => {
    generateSpy.mockResolvedValue(response());
    setup({ task: 'Existing' });
    await generate();
    expect(generateSpy).toHaveBeenCalledWith({ prompt: 'Analyst reads C:\\Logs on SRV-01', currentConfig: { task: 'Existing' } });
  });

  it('keeps Draft team disabled until there is a description', () => {
    setup();
    expect(screen.getByRole('button', { name: 'Draft team' })).toBeDisabled();
  });

  it('shows effective target, paths and an unrestricted HTTP tool before anything is applied', async () => {
    generateSpy.mockResolvedValue(response());
    const { onApply } = setup();
    await generate();
    expect(screen.getByText(/SRV-01 \(srv01\)/)).toBeInTheDocument();
    expect(screen.getByText(/C:\\Logs/)).toBeInTheDocument();
    expect(screen.getByText(/all hosts/i)).toBeInTheDocument();
    expect(screen.getByText(/No credential/i)).toBeInTheDocument();
    expect(onApply).not.toHaveBeenCalled();
  });

  it('applies the validated patch and reports the team lead', async () => {
    generateSpy.mockResolvedValue(response());
    const { onApply, onClose } = setup();
    await generate();
    fireEvent.click(screen.getByRole('button', { name: 'Apply to node' }));
    expect(onApply).toHaveBeenCalledWith(
      expect.objectContaining({ task: 'Diagnose the service', maxParallelMembers: 1, members: expect.any(Array) }), 'lead');
    expect(onClose).toHaveBeenCalled();
  });

  it('blocks Apply until an unresolved machine is settled, then writes the picked machine', async () => {
    generateSpy.mockResolvedValue(response({
      issues: [{
        severity: 'blocking', memberId: 'analyst', field: 'machine', code: 'unresolved', reference: 'SRV-09',
        message: "Machine 'SRV-09' was not found.", candidates: [{ id: 'm1', name: 'SRV-01', detail: 'srv01' }],
      }],
      patch: { ...response().patch, members: response().patch.members.map(m => m.id === 'analyst' ? { ...m, targetMachineId: undefined } : m) },
    }));
    const { onApply } = setup();
    await generate();
    const apply = screen.getByRole('button', { name: 'Apply to node' });
    expect(apply).toBeDisabled();
    expect(screen.getByText(/1 open point/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Machine 'SRV-09' was not found."), { target: { value: 'pick:m1' } });
    await waitFor(() => expect(apply).toBeEnabled());
    fireEvent.click(apply);
    const members = onApply.mock.calls[0][0].members as Array<{ id: string; targetMachineId?: string }>;
    expect(members.find(m => m.id === 'analyst')?.targetMachineId).toBe('m1');
  });

  it('lets the user drop an unavailable service identity request', async () => {
    generateSpy.mockResolvedValue(response({
      issues: [{
        severity: 'blocking', memberId: 'analyst', field: 'serviceIdentity', code: 'not_available', reference: null,
        message: 'The service identity was requested but is disabled or needs an administrator.', candidates: [],
      }],
    }));
    setup();
    await generate();
    expect(screen.getByRole('button', { name: 'Apply to node' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/service identity was requested/i), { target: { value: 'discard' } });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply to node' })).toBeEnabled());
  });

  it('never enables Apply for a team-level configuration error', async () => {
    generateSpy.mockResolvedValue(response({
      issues: [{ severity: 'blocking', memberId: '', field: 'team', code: 'invalid_config', reference: null, message: 'Agent budgets must be positive.', candidates: [] }],
    }));
    setup();
    await generate();
    expect(screen.getByText('Agent budgets must be positive.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply to node' })).toBeDisabled();
  });

  it('requires confirmation before replacing existing members', async () => {
    generateSpy.mockResolvedValue(response());
    setup({ members: [{ id: 'old', role: 'Old', instructions: '', tools: [], skillIds: [] }] });
    await generate();
    const apply = screen.getByRole('button', { name: 'Apply to node' });
    expect(apply).toBeDisabled();
    fireEvent.click(screen.getByLabelText(/Replace the 1 existing member/));
    expect(apply).toBeEnabled();
  });

  it('shows the error and stays on the description stage when drafting fails', async () => {
    generateSpy.mockRejectedValue(new Error('LLM unreachable'));
    setup();
    fireEvent.change(screen.getByLabelText('Team description'), { target: { value: 'a team' } });
    fireEvent.click(screen.getByRole('button', { name: 'Draft team' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('LLM unreachable');
    expect(screen.queryByText('Review team draft')).not.toBeInTheDocument();
  });
});
