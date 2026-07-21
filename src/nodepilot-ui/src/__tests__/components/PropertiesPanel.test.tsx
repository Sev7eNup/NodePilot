import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import type { Node } from '@xyflow/react';

import { PropertiesPanel } from '../../components/designer/PropertiesPanel';
import { useDesignStore } from '../../stores/designStore';

const BASE = 'http://localhost';
const server = setupServer();

beforeEach(() => {
  useDesignStore.setState({ designerMode: 'expert' });
});

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function makeNode(activityType: string, overrides: Partial<Node['data']> = {}): Node {
  return {
    id: 'step-1',
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { label: 'My Step', activityType, config: {}, ...overrides } as Record<string, unknown>,
  };
}

function renderPanel({
  node = makeNode('runScript'),
  onUpdate = vi.fn(),
  onClose = vi.fn(),
  workflowId = undefined as string | undefined,
} = {}) {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utils = render(
    <QueryClientProvider client={qc}>
      <PropertiesPanel
        node={node}
        allNodes={[node]}
        edges={[]}
        machines={[]}
        credentials={[]}
        onUpdate={onUpdate}
        onClose={onClose}
        workflowId={workflowId}
      />
    </QueryClientProvider>
  );
  return { ...utils, onUpdate, onClose };
}

describe('PropertiesPanel — header', () => {
  it('renders the node label and activity-type label in the sticky header', () => {
    renderPanel({ node: makeNode('runScript', { label: 'Check Disk' }) });

    // InlineEditable renders the name as a click-to-edit button.
    expect(screen.getByRole('button', { name: 'Node name' })).toHaveTextContent('Check Disk');
    expect(screen.getByText('Run Script')).toBeInTheDocument();
  });

  it('shows the "Unnamed" placeholder when the node has no label', () => {
    renderPanel({ node: makeNode('runScript', { label: '' }) });
    expect(screen.getByText('Unnamed')).toBeInTheDocument();
  });

  it('clicking the inline-editable name swaps to an input that updates the label on commit', () => {
    const { onUpdate } = renderPanel({ node: makeNode('runScript', { label: 'Old' }) });

    // Enter edit mode by clicking the InlineEditable button.
    fireEvent.click(screen.getByRole('button', { name: 'Node name' }));
    const input = screen.getByLabelText('Node name') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'New' } });
    fireEvent.blur(input);

    expect(onUpdate).toHaveBeenCalled();
    const [nodeId, patch] = onUpdate.mock.calls.at(-1)!;
    expect(nodeId).toBe('step-1');
    expect(patch).toMatchObject({ label: 'New', activityType: 'runScript' });
  });

  it('calls onClose when the close button is clicked', () => {
    const { onClose } = renderPanel();
    fireEvent.click(screen.getByLabelText('Close properties panel'));
    expect(onClose).toHaveBeenCalledOnce();
  });
});

describe('PropertiesPanel — description (lazy reveal)', () => {
  it('shows an "Add description" link when description is empty', () => {
    renderPanel();
    expect(screen.getByRole('button', { name: /Add description/i })).toBeInTheDocument();
    expect(screen.queryByPlaceholderText(/Optional step description/i)).not.toBeInTheDocument();
  });

  it('clicking the link reveals the textarea', () => {
    renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /Add description/i }));
    expect(screen.getByPlaceholderText(/Optional step description/i)).toBeInTheDocument();
  });

  it('shows the textarea immediately when description is non-empty', () => {
    renderPanel({ node: makeNode('runScript', { description: 'Existing notes' }) });
    expect(screen.getByDisplayValue('Existing notes')).toBeInTheDocument();
  });

  it('typing in the description textarea calls onUpdate', () => {
    const { onUpdate } = renderPanel({ node: makeNode('runScript', { description: 'x' }) });

    const textarea = screen.getByPlaceholderText(/Optional step description/i);
    fireEvent.change(textarea, { target: { value: 'Disk health' } });

    expect(onUpdate.mock.calls.at(-1)![1]).toMatchObject({ description: 'Disk health' });
  });
});

describe('PropertiesPanel — status pills', () => {
  it('standardMode_hidesBreakpointButKeepsCoreStatusControls', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    renderPanel();

    expect(screen.getByRole('button', { name: /^Active$/ })).toBeInTheDocument();
    expect(screen.getByText('step-1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /No break/i })).not.toBeInTheDocument();
  });

  it('clicking the Active pill toggles data.disabled to true', () => {
    const { onUpdate } = renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /^Active$/ }));
    expect(onUpdate.mock.calls.at(-1)![1]).toMatchObject({ disabled: true });
  });

  it('clicking the Disabled pill toggles data.disabled back to false', () => {
    const { onUpdate } = renderPanel({ node: makeNode('runScript', { disabled: true }) });
    fireEvent.click(screen.getByRole('button', { name: /^Disabled$/ }));
    expect(onUpdate.mock.calls.at(-1)![1]).toMatchObject({ disabled: false });
  });

  it('clicking the No-Break pill sets data.breakpoint to true', () => {
    const { onUpdate } = renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /No break/i }));
    expect(onUpdate.mock.calls.at(-1)![1]).toMatchObject({ breakpoint: true });
  });

  it('shows the step-id as the placeholder in the Output-Var pill when no outputVariable is set', () => {
    renderPanel();
    // Pill shows `→ step-1` when outputVariable is empty (using the node id).
    expect(screen.getByText('step-1')).toBeInTheDocument();
  });

  it('clicking the Output-Var pill reveals an input that sanitizes non-alphanumerics', () => {
    const { onUpdate } = renderPanel({ node: makeNode('runScript', { outputVariable: '' }) });
    // Click the pill to open the popover (find by step-1 placeholder text inside the pill).
    fireEvent.click(screen.getByText('step-1').closest('button')!);

    const input = screen.getByPlaceholderText('step-1') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'my var!' } });

    // Sanitize regex strips spaces and `!`.
    expect(onUpdate.mock.calls.at(-1)![1]).toMatchObject({ outputVariable: 'myvar' });
  });
});

describe('PropertiesPanel — sections & gating', () => {
  it('renders the Test & Debug section + Run-test button only when a workflowId is provided and the node is not a trigger', () => {
    const { rerender } = renderPanel({ workflowId: 'wf-1' });
    // Test & Debug section is collapsible defaultOpen=false — expand first.
    fireEvent.click(screen.getByText('Test & Debug'));
    // After the rework the button label is "Run test (<mode>)". Default mode = Empty.
    expect(screen.getByRole('button', { name: /Run test/i })).toBeInTheDocument();

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    rerender(
      <QueryClientProvider client={qc}>
        <PropertiesPanel
          node={makeNode('manualTrigger')}
          allNodes={[]}
          edges={[]}
          machines={[]}
          credentials={[]}
          onUpdate={vi.fn()}
          onClose={vi.fn()}
          workflowId="wf-1"
        />
      </QueryClientProvider>
    );
    // Manual trigger has no Test & Debug section at all.
    expect(screen.queryByText('Test & Debug')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Run test/i })).not.toBeInTheDocument();
  });

  it('shows the trigger-active hint banner for an external trigger activity', () => {
    renderPanel({ node: makeNode('scheduleTrigger') });
    expect(screen.getByText(/monitored automatically by TriggerOrchestrator|wird vom TriggerOrchestrator automatisch/i)).toBeInTheDocument();
  });

  it('renders the runScript-specific config (PowerShell Script + Engine) inside the Configuration section', () => {
    renderPanel({ node: makeNode('runScript', { config: { script: 'Get-PSDrive C' } }) });

    // Configuration section header is visible.
    expect(screen.getByText('Configuration')).toBeInTheDocument();
    // Engine selector and PowerShell Script label are rendered.
    expect(screen.getByText('Engine')).toBeInTheDocument();
    expect(screen.getByText('PowerShell Script')).toBeInTheDocument();
    // Open Editor opens the fullscreen ScriptEditorDialog (still wired up).
    expect(screen.getByRole('button', { name: /Open Editor/i })).toBeInTheDocument();
  });

  it('does not render runScript-specific config for a different activity type', () => {
    renderPanel({ node: makeNode('delay', { config: { seconds: 5 } }) });
    expect(screen.queryByText('PowerShell Script')).not.toBeInTheDocument();
    expect(screen.queryByText('Engine')).not.toBeInTheDocument();
  });
});

describe('PropertiesPanel — read-only mode (canWrite=false)', () => {
  function renderReadOnly(node: Node = makeNode('runScript', { config: { script: 'Get-Date' } })) {
    patchFetch();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <QueryClientProvider client={qc}>
        <PropertiesPanel
          node={node}
          allNodes={[node]}
          edges={[]}
          machines={[]}
          credentials={[]}
          onUpdate={vi.fn()}
          onClose={vi.fn()}
          canWrite={false}
        />
      </QueryClientProvider>
    );
  }

  it('renders the node name as a non-interactive span (no edit button) when canWrite=false', () => {
    renderReadOnly(makeNode('runScript', { label: 'Read Me' }));
    // InlineEditable in disabled mode renders a span instead of a button.
    expect(screen.queryByRole('button', { name: 'Node name' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Node name')).toHaveTextContent('Read Me');
  });

  it('disables every form input/select/button under the panel body via fieldset[disabled]', () => {
    const { container } = renderReadOnly();
    // The Engine <select> + Auto-Logging <input type="checkbox"> live in RunScriptConfig.
    const engineSelect = container.querySelector('select') as HTMLSelectElement | null;
    expect(engineSelect).not.toBeNull();
    expect(engineSelect).toBeDisabled();

    // Status-pill buttons (Active/No-Break) inside the header status row.
    const activePill = screen.queryByRole('button', { name: /^Active$/ });
    expect(activePill).toBeDisabled();
    const noBreakPill = screen.queryByRole('button', { name: /No break/i });
    expect(noBreakPill).toBeDisabled();
  });

  it('keeps the close button enabled even in read-only mode', () => {
    renderReadOnly();
    const closeBtn = screen.getByLabelText('Close properties panel');
    expect(closeBtn).not.toBeDisabled();
  });
});
