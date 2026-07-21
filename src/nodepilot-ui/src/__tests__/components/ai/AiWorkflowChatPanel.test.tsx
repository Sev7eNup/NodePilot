import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import type { Edge, Node } from '@xyflow/react';
import { AiWorkflowChatPanel } from '../../../components/ai/AiWorkflowChatPanel';
import { aiApi, type WorkflowChatRequest, type ChatStreamHandlers } from '../../../api/ai';
import { useAiChatStore } from '../../../stores/aiChatStore';
import { useAuthStore } from '../../../stores/authStore';
import { hashDefinition } from '../../../lib/workflowDiff';

vi.mock('../../../api/ai', async (orig) => {
  const actual = await orig<typeof import('../../../api/ai')>();
  return { ...actual, aiApi: { ...actual.aiApi, chatStream: vi.fn(), chatApplied: vi.fn(), chatActivity: vi.fn() } };
});

const chatMock = aiApi.chatStream as unknown as ReturnType<typeof vi.fn>;
const appliedMock = aiApi.chatApplied as unknown as ReturnType<typeof vi.fn>;
const activityMock = aiApi.chatActivity as unknown as ReturnType<typeof vi.fn>;

const baseNodes: Node[] = [
  { id: 't1', type: 'activity', position: { x: 0, y: 0 }, data: { activityType: 'manualTrigger', label: 'Start', config: {} } },
];
const baseDef = () => ({ nodes: baseNodes.map((n) => ({ ...n })), edges: [] as Edge[] });

const proposalDefinitionJson = JSON.stringify({
  nodes: [
    ...baseNodes,
    { id: 'l1', type: 'activity', position: { x: 300, y: 0 }, data: { activityType: 'log', label: 'Log', config: { message: 'hi' } } },
  ],
  edges: [{ id: 'e1', source: 't1', target: 'l1', type: 'labeled', data: {} }],
});

/** Drives the streaming handlers: sends text deltas, and optionally a proposal (which echoes
 *  back the request's baseDefinitionHash).
 *  The `if (!h) return` guard exists because vitest's module-mock spread calls this mock
 *  implementation a second time under `act()` without a handlers object — in the real component,
 *  `handleSend` (and thus the single `chatStream` call) only fires once. */
function streamMock(opts: { reply: string; proposal?: boolean }) {
  return (req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
    if (!h) return Promise.resolve();
    h.onDelta(opts.reply);
    if (opts.proposal) {
      h.onProposal({ definitionJson: proposalDefinitionJson, summary: '', nodeCount: 2, edgeCount: 1, baseDefinitionHash: req.baseDefinitionHash });
    }
    return Promise.resolve();
  };
}

function setup(over: Partial<Parameters<typeof AiWorkflowChatPanel>[0]> = {}) {
  const applyDefinition = vi.fn();
  let currentDef = baseDef();
  // A fresh arrow function is created on every render, matching real usage: WorkflowEditorPage
  // recreates getCurrentDefinition via useCallback([nodes, edges]) on every canvas change, and
  // MessageBubble memoizes its diff base on that function identity.
  const element = () => (
    <AiWorkflowChatPanel
      workflowId="wf-1"
      getCurrentDefinition={() => currentDef}
      applyDefinition={applyDefinition}
      canApply={true}
      isViewer={false}
      onClose={() => {}}
      {...over}
    />
  );
  const utils = render(element());
  return {
    applyDefinition,
    unmount: utils.unmount,
    setCurrentDef: (d: ReturnType<typeof baseDef>) => { currentDef = d; utils.rerender(element()); },
  };
}

async function ask(question: string) {
  fireEvent.change(screen.getByRole('textbox'), { target: { value: question } });
  fireEvent.click(screen.getByRole('button', { name: /send/i }));
}

beforeEach(() => {
  chatMock.mockReset();
  appliedMock.mockReset();
  appliedMock.mockResolvedValue(undefined); // handleApplied does `.catch()` on the returned promise
  activityMock.mockReset();
  activityMock.mockResolvedValue([]);
  // Chat history lives in a module-global store, so it must be cleared between tests or threads
  // would leak from one test into the next.
  useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
  useAuthStore.setState({ userId: null });
});

describe('AiWorkflowChatPanel (streaming)', () => {
  it('streams a question and renders the markdown reply', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'This workflow **starts manually**.' }));
    setup();

    await ask('What does this do?');

    await waitFor(() => expect(screen.getByText(/starts manually/i)).toBeInTheDocument());
    const req = chatMock.mock.calls[0][0] as WorkflowChatRequest;
    expect(req.question).toBe('What does this do?');
    expect(req.baseDefinitionHash).toMatch(/^[0-9a-f]{8}$/);
    expect(req.workflowJson).toContain('manualTrigger');
  });

  it('renders read-only tool activity when the assistant calls a tool', async () => {
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onToolCall?.('analyze_workflow', 'c1');
      h.onToolResult?.('analyze_workflow', 'c1');
      h.onDelta('All good.');
      return Promise.resolve();
    });
    setup();

    await ask('Review please.');

    await waitFor(() => expect(screen.getByText('analyze_workflow')).toBeInTheDocument());
    expect(screen.getByText(/checked/i)).toBeInTheDocument();
  });

  it('isolates threads — "New chat" starts an empty thread without bleeding messages', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'First answer.' }));
    setup();

    await ask('First question');
    await waitFor(() => expect(screen.getByText('First answer.')).toBeInTheDocument());

    // Open the thread switcher (aria-label = "Chats") and create a new chat.
    fireEvent.click(screen.getByRole('button', { name: /chats/i }));
    fireEvent.click(screen.getByText('New chat'));

    // The new thread is empty — the previous answer is no longer shown.
    await waitFor(() => expect(screen.queryByText('First answer.')).not.toBeInTheDocument());
  });

  it('exports the active thread as Markdown', async () => {
    const createObjectURL = vi.fn(() => 'blob:x');
    const origCreate = URL.createObjectURL;
    const origRevoke = URL.revokeObjectURL;
    URL.createObjectURL = createObjectURL as unknown as typeof URL.createObjectURL;
    URL.revokeObjectURL = vi.fn();
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    try {
      chatMock.mockImplementation(streamMock({ reply: 'Runs daily.' }));
      setup();
      await ask('What does this do?');
      await waitFor(() => expect(screen.getByText('Runs daily.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: /export as markdown/i }));

      expect(createObjectURL).toHaveBeenCalledTimes(1);
      expect(clickSpy).toHaveBeenCalled();
    } finally {
      clickSpy.mockRestore();
      URL.createObjectURL = origCreate;
      URL.revokeObjectURL = origRevoke;
    }
  });

  it('audits an applied proposal via chatApplied (workflow + counts)', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    setup({ workflowId: 'wf-1' });

    await ask('Add a log step.');
    const applyBtn = await screen.findByRole('button', { name: /^apply$/i });
    fireEvent.click(applyBtn);

    await waitFor(() => expect(appliedMock).toHaveBeenCalledTimes(1));
    expect(appliedMock).toHaveBeenCalledWith(expect.objectContaining({ workflowId: 'wf-1', nodeCount: 2, edgeCount: 1 }));
  });

  it('loads and renders workflow AI activity in the header dropdown', async () => {
    activityMock.mockResolvedValue([
      { timestamp: '2026-01-02T00:00:00Z', userId: 'u1', username: 'alice', action: 'AI_PROPOSAL_APPLIED', details: null },
      { timestamp: '2026-01-01T00:00:00Z', userId: 'u1', username: 'bob', action: 'AI_WORKFLOW_EXPLAINED', details: null },
    ]);
    setup({ workflowId: 'wf-1' });

    fireEvent.click(screen.getByRole('button', { name: /ai activity/i }));

    await waitFor(() => expect(screen.getByText('Applied')).toBeInTheDocument());
    expect(screen.getByText('Asked')).toBeInTheDocument();
    expect(activityMock).toHaveBeenCalledWith('wf-1');
  });

  it('hides the activity menu for viewers (the API forbids them anyway)', () => {
    setup({ workflowId: 'wf-1', isViewer: true });
    expect(screen.queryByRole('button', { name: /ai activity/i })).not.toBeInTheDocument();
  });

  it('locks thread delete/rename/switch while a stream is in flight', async () => {
    let release: () => void = () => {};
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onDelta('partial…');
      return new Promise<void>((res) => { release = res; }); // keep sending=true until released
    });
    setup();

    await ask('Question that streams');
    // Open the thread switcher (the menu toggle is not disabled, only the actions are).
    fireEvent.click(screen.getByRole('button', { name: /chats/i }));

    await waitFor(() => expect(screen.getByRole('button', { name: /delete chat/i })).toBeDisabled());
    expect(screen.getByRole('button', { name: /^rename$/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /new chat/i })).toBeDisabled();

    release(); // let the stream finish so the test tears down cleanly
  });

  it('shows a proposal with an Apply button and applies to the canvas', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    const { applyDefinition } = setup();

    await ask('Add a log step.');
    const applyBtn = await screen.findByRole('button', { name: /^apply$/i });
    fireEvent.click(applyBtn);

    expect(applyDefinition).toHaveBeenCalledTimes(1);
    expect(applyDefinition.mock.calls[0][0].nodes.map((n: Node) => n.id)).toContain('l1');
  });

  it('discards a proposal via the Verwerfen button', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    const { applyDefinition } = setup();

    await ask('Add a log step.');
    await screen.findByRole('button', { name: /^apply$/i });
    fireEvent.click(screen.getByRole('button', { name: /discard/i }));

    await waitFor(() => expect(screen.getByText(/proposal discarded/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^apply$/i })).not.toBeInTheDocument();
    expect(applyDefinition).not.toHaveBeenCalled();
  });

  it('shows a "generating change" indicator after the prose while the definition builds', async () => {
    let resolveStream!: () => void;
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onDelta('I will add a log step.');
      h.onBuilding?.();
      return new Promise<void>((r) => { resolveStream = r; });
    });
    setup();

    await ask('Add a log step.');
    await waitFor(() => expect(screen.getByText(/generating workflow change/i)).toBeInTheDocument());
    resolveStream();
  });

  it('hides Apply for a viewer and shows the reserved-for hint', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Would add a log step.', proposal: true }));
    setup({ canApply: false, isViewer: true });

    await ask('Add a log step.');
    await waitFor(() => expect(screen.getByText(/reserved for Operator\/Admin/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^apply$/i })).not.toBeInTheDocument();
  });

  it('blocks Apply when the canvas changed since the proposal (stale guard)', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    const { applyDefinition, setCurrentDef } = setup();

    await ask('Add a log step.');
    const applyBtn = await screen.findByRole('button', { name: /^apply$/i });
    setCurrentDef({ nodes: [{ ...baseNodes[0], position: { x: 500, y: 0 } }], edges: [] });
    fireEvent.click(applyBtn);

    expect(applyDefinition).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
  });

  it('shows an error when the stream fails', async () => {
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) =>
      h ? Promise.reject(new Error('LLM endpoint unreachable')) : Promise.resolve());
    setup();

    await ask('explain');
    expect(await screen.findByRole('alert')).toHaveTextContent(/unreachable/i);
  });

  it('does NOT show an error when the user stops the stream (AbortError)', async () => {
    const abortErr = new DOMException('aborted', 'AbortError');
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onDelta('partial answer');
      return Promise.reject(abortErr);
    });
    setup();

    await ask('explain');
    await waitFor(() => expect(screen.getByText(/partial answer/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends on Enter and inserts a newline on Shift+Enter', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'ok reply' }));
    setup();

    const box = screen.getByRole('textbox');
    fireEvent.change(box, { target: { value: 'line one' } });
    fireEvent.keyDown(box, { key: 'Enter', shiftKey: true });
    expect(chatMock).not.toHaveBeenCalled();

    fireEvent.keyDown(box, { key: 'Enter' });
    await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(1));
    expect((chatMock.mock.calls[0][0] as WorkflowChatRequest).question).toBe('line one');
  });

  it('regenerates the last answer (re-asks the same question, replaces the bubble)', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'first answer' }));
    setup();
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/first answer/i)).toBeInTheDocument());

    chatMock.mockImplementation(streamMock({ reply: 'second answer' }));
    fireEvent.click(screen.getByRole('button', { name: /regenerate/i }));

    await waitFor(() => expect(screen.getByText(/second answer/i)).toBeInTheDocument());
    expect(chatMock).toHaveBeenCalledTimes(2);
    expect((chatMock.mock.calls[1][0] as WorkflowChatRequest).question).toBe('explain');
    expect(screen.queryByText(/first answer/i)).not.toBeInTheDocument();
  });

  it('retries from the error banner after a failed stream', async () => {
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) =>
      h ? Promise.reject(new Error('LLM down')) : Promise.resolve());
    setup();
    await ask('explain');
    expect(await screen.findByRole('alert')).toHaveTextContent(/LLM down/i);

    chatMock.mockImplementation(streamMock({ reply: 'recovered' }));
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    await waitFor(() => expect(screen.getByText(/recovered/i)).toBeInTheDocument());
    expect(chatMock).toHaveBeenCalledTimes(2);
  });

  it('sends a starter suggestion from the empty state', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'ok' }));
    setup();

    fireEvent.click(screen.getByRole('button', { name: /explain this workflow/i }));

    await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(1));
    expect((chatMock.mock.calls[0][0] as WorkflowChatRequest).question).toBe('Explain this workflow');
  });

  it('copies an assistant answer to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    chatMock.mockImplementation(streamMock({ reply: 'copy me' }));
    setup();
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/copy me/i)).toBeInTheDocument());

    fireEvent.click(screen.getAllByRole('button', { name: /^copy$/i })[0]);
    expect(writeText).toHaveBeenCalledWith('copy me');
  });

  it('renders the usage footer (model + tokens + tok/s) after the stream completes', async () => {
    chatMock.mockImplementation((_req: WorkflowChatRequest, h?: ChatStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onDelta('done answer');
      // 50 completion tokens in 1000 ms → 50 tok/s.
      h.onDone?.({ model: 'gpt-x', durationMs: 1000, promptTokens: 100, completionTokens: 50 });
      return Promise.resolve();
    });
    setup();
    await ask('explain');

    await waitFor(() => expect(screen.getByText(/gpt-x/i)).toBeInTheDocument());
    expect(screen.getByText(/150 tokens/i)).toBeInTheDocument();
    expect(screen.getByText(/50 tok\/s/i)).toBeInTheDocument();
  });

  it('shows a node mention popup on "@" and inserts the label', async () => {
    setup();
    const box = screen.getByRole('textbox');
    fireEvent.change(box, { target: { value: '@', selectionStart: 1 } });

    fireEvent.mouseDown(await screen.findByRole('button', { name: /start/i }));

    await waitFor(() => expect((screen.getByRole('textbox') as HTMLTextAreaElement).value).toContain('@`Start`'));
  });

  it('keeps the thread for the same user+workflow across remount', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'remembered' }));
    const { unmount } = setup({ workflowId: 'wf-1' });
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/remembered/i)).toBeInTheDocument());

    unmount();
    setup({ workflowId: 'wf-1' });
    expect(screen.getByText(/remembered/i)).toBeInTheDocument();
  });

  it('isolates threads per user (no cross-user leak)', async () => {
    useAuthStore.setState({ userId: 'user-a' });
    chatMock.mockImplementation(streamMock({ reply: 'a-secret' }));
    const { unmount } = setup({ workflowId: 'wf-1' });
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/a-secret/i)).toBeInTheDocument());

    unmount();
    useAuthStore.setState({ userId: 'user-b' });
    setup({ workflowId: 'wf-1' });
    expect(screen.queryByText(/a-secret/i)).not.toBeInTheDocument();
  });

  // ---- Smarter proposals: structured changelog, selective apply, refine ---------

  it('renders a structured changelog for the proposal', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    setup();
    await ask('Add a log step.');
    await screen.findByRole('button', { name: /^apply$/i });
    // The added node 'l1' (label "Log") shows up in the changelog list.
    expect(screen.getByText('Log')).toBeInTheDocument();
    expect(screen.getAllByRole('checkbox')).toHaveLength(2); // add node + add edge
  });

  it('selective apply: unchecking the edge applies only the node', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    const { applyDefinition } = setup();
    await ask('Add a log step.');
    const applyBtn = await screen.findByRole('button', { name: /^apply$/i });
    const checks = screen.getAllByRole('checkbox');
    fireEvent.click(checks[1]); // uncheck the added edge
    fireEvent.click(applyBtn);

    expect(applyDefinition).toHaveBeenCalledTimes(1);
    const arg = applyDefinition.mock.calls[0][0] as { nodes: Node[]; edges: Edge[] };
    expect(arg.nodes.map((n) => n.id)).toContain('l1');
    expect(arg.edges).toHaveLength(0); // edge was not selected
  });

  it('refine sends the prior proposal as LLM base but keeps the real-canvas hash', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    setup();
    await ask('Add a log step.');
    await screen.findByRole('button', { name: /^apply$/i });

    fireEvent.click(screen.getByRole('button', { name: /^refine$/i }));
    chatMock.mockImplementation(streamMock({ reply: 'refined' }));
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'make it warn' } });
    fireEvent.click(screen.getByRole('button', { name: /send/i }));

    await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(2));
    const req1 = chatMock.mock.calls[0][0] as WorkflowChatRequest;
    const req2 = chatMock.mock.calls[1][0] as WorkflowChatRequest;
    expect(req1.workflowJson).not.toContain('l1');     // first turn: bare canvas
    expect(req2.workflowJson).toContain('l1');          // refine turn: the proposal as base
    expect(req2.baseDefinitionHash).toBe(req1.baseDefinitionHash); // stale-guard = real canvas
  });

  it('offers Undo and Auto-Layout after applying', async () => {
    const onUndo = vi.fn();
    const onAutoLayout = vi.fn();
    chatMock.mockImplementation(streamMock({ reply: 'Added a log step.', proposal: true }));
    setup({ onUndo, onAutoLayout });
    await ask('Add a log step.');
    fireEvent.click(await screen.findByRole('button', { name: /^apply$/i }));

    fireEvent.click(screen.getByRole('button', { name: /tidy layout/i }));
    expect(onAutoLayout).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: /^undo$/i }));
    expect(onUndo).toHaveBeenCalledTimes(1);
  });

  it('scopes the question to the selection when the chip is active', async () => {
    chatMock.mockImplementation(streamMock({ reply: 'ok' }));
    setup({ selection: { nodeLabels: ['Check disk'], edgeCount: 0 } });
    fireEvent.click(screen.getByRole('button', { name: /selection \(1\)/i }));
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'what does this do' } });
    fireEvent.click(screen.getByRole('button', { name: /send/i }));

    await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(1));
    const req = chatMock.mock.calls[0][0] as WorkflowChatRequest;
    expect(req.question).toContain('Check disk');
    expect(req.question).toContain('what does this do');
  });

  it('trims the request history to the backend turn cap', async () => {
    const longThread = Array.from({ length: 24 }, (_, i) => ({
      role: (i % 2 === 0 ? 'user' : 'assistant') as 'user' | 'assistant', content: `m${i}`,
    }));
    const scope = 'anon::wf-1';
    const threadId = 'seed';
    useAiChatStore.setState({
      messagesByThread: { [`${scope}::${threadId}`]: longThread },
      threadsByScope: { [scope]: [{ id: threadId, name: 'Chat 1', createdAt: 1, updatedAt: 1 }] },
      activeThreadByScope: { [scope]: threadId },
    });
    chatMock.mockImplementation(streamMock({ reply: 'ok' }));
    setup({ workflowId: 'wf-1' });
    await ask('new question');

    await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(1));
    expect((chatMock.mock.calls[0][0] as WorkflowChatRequest).history.length).toBeLessThanOrEqual(19);
  });
});

describe('AiWorkflowChatPanel (proposal reload survival)', () => {
  /** Simulates a persisted thread restored after a page reload: the proposal JSON survives,
   *  but the base-definition snapshot has been stripped out. */
  function seedPersistedProposal(baseDefinitionHash: string) {
    const scope = 'anon::wf-1';
    const threadId = 'seed';
    useAiChatStore.setState({
      messagesByThread: {
        [`${scope}::${threadId}`]: [
          { role: 'user', content: 'Add a log step.' },
          {
            role: 'assistant',
            content: 'Added a log step.',
            proposal: { definitionJson: proposalDefinitionJson, summary: '', nodeCount: 2, edgeCount: 1, baseDefinitionHash },
          },
        ],
      },
      threadsByScope: { [scope]: [{ id: threadId, name: 'Chat 1', createdAt: 1, updatedAt: 1 }] },
      activeThreadByScope: { [scope]: threadId },
    });
  }

  it('restores an applicable proposal after reload when the canvas still matches its base hash', async () => {
    seedPersistedProposal(hashDefinition(baseDef()));
    const { applyDefinition } = setup({ workflowId: 'wf-1' });

    const applyBtn = await screen.findByRole('button', { name: /^apply$/i });
    expect(screen.queryByText(/no longer applicable/i)).not.toBeInTheDocument();

    fireEvent.click(applyBtn);
    await waitFor(() => expect(applyDefinition).toHaveBeenCalledTimes(1));
  });

  it('shows the expired hint after reload when the canvas moved past the proposal base', async () => {
    seedPersistedProposal(hashDefinition(baseDef()));
    const { setCurrentDef } = setup({ workflowId: 'wf-1' });
    setCurrentDef({ nodes: [{ ...baseNodes[0], position: { x: 500, y: 0 } }], edges: [] });

    // Force a re-render (the canvas getter is read while the message bubble renders).
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'x' } });

    await waitFor(() => expect(screen.getByText(/no longer applicable/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^apply$/i })).not.toBeInTheDocument();
  });

  it('shows the expired hint when the persisted proposal JSON was dropped (over the cap)', async () => {
    const scope = 'anon::wf-1';
    const threadId = 'seed';
    useAiChatStore.setState({
      messagesByThread: {
        [`${scope}::${threadId}`]: [{
          role: 'assistant',
          content: 'Added a log step.',
          proposal: { definitionJson: '', summary: '', nodeCount: 2, edgeCount: 1, baseDefinitionHash: hashDefinition(baseDef()) },
        }],
      },
      threadsByScope: { [scope]: [{ id: threadId, name: 'Chat 1', createdAt: 1, updatedAt: 1 }] },
      activeThreadByScope: { [scope]: threadId },
    });
    setup({ workflowId: 'wf-1' });

    await waitFor(() => expect(screen.getByText(/no longer applicable/i)).toBeInTheDocument());
  });
});
