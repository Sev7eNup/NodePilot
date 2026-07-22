import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { CloneConfigButton } from '../../../components/designer/properties/CloneConfigButton';

function activityNode(id: string, data: Record<string, unknown>): Node {
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { label: id, activityType: 'runScript', config: {}, ...data },
  } as unknown as Node;
}

beforeEach(() => {
  vi.useFakeTimers().setSystemTime(new Date('2026-05-07T12:00:00Z'));
});
afterEach(() => {
  vi.useRealTimers();
});

describe('CloneConfigButton — visibility', () => {
  it('does not render when no candidates exist (only the current node)', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const { container } = render(
      <CloneConfigButton currentNode={current} allNodes={[current]} onClone={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders when at least one same-type candidate exists', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1', credentialId: 'c-1' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);
    expect(screen.getByTestId('clone-config-button')).toBeInTheDocument();
  });

  it('renders the translated (EN) button label, not a hardcoded German string', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);
    expect(screen.getByText('Adopt config from…')).toBeInTheDocument();
    expect(screen.queryByText('Config übernehmen von…')).not.toBeInTheDocument();
  });

  it('renders for remote-only candidates even with different activity types', () => {
    const current = activityNode('s1', { activityType: 'serviceManagement' });
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1', credentialId: 'c-1' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);
    expect(screen.getByTestId('clone-config-button')).toBeInTheDocument();
  });

  it('does not render when source remote nodes have no machine set', () => {
    const current = activityNode('s1', { activityType: 'serviceManagement' });
    // Different type, but the only remote sibling has no machine — clone would copy null.
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: null });
    const { container } = render(
      <CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('ignores sticky-note + group nodes as candidates', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const note = { id: 'n1', type: 'stickyNote', position: { x: 0, y: 0 }, data: { activityType: 'note' } } as unknown as Node;
    const group = { id: 'g1', type: 'group', position: { x: 0, y: 0 }, data: { activityType: 'group' } } as unknown as Node;
    const { container } = render(
      <CloneConfigButton currentNode={current} allNodes={[current, note, group]} onClone={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });
});

describe('CloneConfigButton — popover', () => {
  it('opens the popover on click and lists same-type candidates', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling1 = activityNode('s2', {
      label: 'Daily Health Check', activityType: 'runScript',
      targetMachineId: 'machine-A', credentialId: 'cred-1',
      config: { script: 'Get-Date', timeoutSeconds: 60 },
    });
    const sibling2 = activityNode('s3', {
      label: 'Disk Cleanup', activityType: 'runScript',
      targetMachineId: 'machine-B',
      config: { script: 'whoami' },
    });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling1, sibling2]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    expect(screen.getByTestId('clone-config-popover')).toBeInTheDocument();
    expect(screen.getByText('Daily Health Check')).toBeInTheDocument();
    expect(screen.getByText('Disk Cleanup')).toBeInTheDocument();
  });

  it('closes when clicking the trigger again', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    expect(screen.getByTestId('clone-config-popover')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('clone-config-button'));
    expect(screen.queryByTestId('clone-config-popover')).not.toBeInTheDocument();
  });
});

describe('CloneConfigButton — clone action', () => {
  it('calls onClone with the full source config (incl. script body) when a candidate is clicked', () => {
    const onClone = vi.fn();
    const current = activityNode('s1', {
      activityType: 'runScript',
      label: 'Target Step', outputVariable: 'targetOut',
      targetMachineId: null, credentialId: null,
      config: { script: 'echo target' },
    });
    const sibling = activityNode('s2', {
      activityType: 'runScript',
      label: 'Source Step', outputVariable: 'sourceOut',
      targetMachineId: 'machine-A', credentialId: 'cred-1',
      config: { script: 'echo source', timeoutSeconds: 60, engine: 'pwsh' },
    });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={onClone} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    fireEvent.click(screen.getByTestId('clone-source-s2'));

    expect(onClone).toHaveBeenCalledOnce();
    const patched = onClone.mock.calls[0][0] as Record<string, unknown>;
    expect(patched.targetMachineId).toBe('machine-A');
    expect(patched.credentialId).toBe('cred-1');
    // Full source config replaces target config — including the script body
    expect((patched.config as Record<string, unknown>).script).toBe('echo source');
    expect((patched.config as Record<string, unknown>).timeoutSeconds).toBe(60);
    expect((patched.config as Record<string, unknown>).engine).toBe('pwsh');
    // Step identity (label + outputVariable) stays
    expect(patched.label).toBe('Target Step');
    expect(patched.outputVariable).toBe('targetOut');
  });

  it('shows a confirmation badge briefly after cloning', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling = activityNode('s2', {
      activityType: 'runScript',
      targetMachineId: 'machine-A',
    });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    fireEvent.click(screen.getByTestId('clone-source-s2'));

    expect(screen.getByText('Adopted')).toBeInTheDocument();
    // Badge resets after the timeout fires
    act(() => { vi.advanceTimersByTime(2000); });
    expect(screen.queryByText('Adopted')).not.toBeInTheDocument();
  });

  it('closes the popover after a clone', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sibling = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-A' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    fireEvent.click(screen.getByTestId('clone-source-s2'));
    expect(screen.queryByTestId('clone-config-popover')).not.toBeInTheDocument();
  });
});

describe('CloneConfigButton — scope toggle', () => {
  it('shows the scope toggle when both same-type and remote-only candidates are available', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sameType = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1' });
    const remoteOther = activityNode('s3', { activityType: 'serviceManagement', targetMachineId: 'm-2' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sameType, remoteOther]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    expect(screen.getByText(/Full.*same type/i)).toBeInTheDocument();
    expect(screen.getByText(/Machine \+ credential only/i)).toBeInTheDocument();
  });

  it('switching to remoteOnly scope expands candidate list to other remote types', () => {
    const current = activityNode('s1', { activityType: 'runScript' });
    const sameType = activityNode('s2', { activityType: 'runScript', targetMachineId: 'm-1' });
    const remoteOther = activityNode('s3', {
      activityType: 'serviceManagement', label: 'Service-Step', targetMachineId: 'm-2',
    });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sameType, remoteOther]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    // Default scope = "all", only same-type appears
    expect(screen.queryByText('Service-Step')).not.toBeInTheDocument();

    // Switch scope
    fireEvent.click(screen.getByText(/Machine \+ credential only/i));
    expect(screen.getByText('Service-Step')).toBeInTheDocument();
  });

  it('does not show scope toggle for engine-local activity types (no remoteOnly path)', () => {
    const current = activityNode('s1', { activityType: 'restApi' });
    const sibling = activityNode('s2', { activityType: 'restApi' });
    render(<CloneConfigButton currentNode={current} allNodes={[current, sibling]} onClone={vi.fn()} />);

    fireEvent.click(screen.getByTestId('clone-config-button'));
    expect(screen.queryByText(/Machine \+ credential only/i)).not.toBeInTheDocument();
  });
});
