import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Link, MemoryRouter, Route, Routes } from 'react-router';
import { AiChatWidget } from '../../../components/ai/AiChatWidget';
import { AiChatPage } from '../../../pages/AiChatPage';
import { askStream, getKnowledgeCapabilities, type KnowledgeStreamHandlers } from '../../../api/ai';
import { useAiChatStore } from '../../../stores/aiChatStore';
import { useKnowledgeChatSessionStore as session } from '../../../stores/knowledgeChatSessionStore';
import { useAuthStore } from '../../../stores/authStore';
import { useChatLayoutStore, DEFAULT_WIDGET_WIDTH } from '../../../stores/chatLayoutStore';

vi.mock('../../../api/ai', async (original) => ({
  ...await original<typeof import('../../../api/ai')>(),
  askStream: vi.fn(), getKnowledgeCapabilities: vi.fn(),
}));

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <div id="np-app-content">
          <Link to="/workflows">Workflows</Link>
          <Link to="/workflows/w-1">Open designer</Link>
          <Routes>
            <Route path="/" element={<p>Dashboard</p>} />
            <Route path="/workflows" element={<p>Workflow list</p>} />
            <Route path="/workflows/:id" element={<p>Designer</p>} />
            <Route path="/ai-chat" element={<AiChatPage />} />
          </Routes>
        </div>
        <AiChatWidget />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  session.getState().reset();
  useAiChatStore.getState().clearAll();
  useAuthStore.setState({ userId: 'widget-user' });
  vi.mocked(getKnowledgeCapabilities).mockResolvedValue({ enabled: true, llm: true, docs: true, operational: true, db: false, sourceCode: false });
  vi.mocked(askStream).mockReset();
  // The panel width persists to localStorage, so reset it between tests.
  useChatLayoutStore.setState({ widgetWidth: DEFAULT_WIDGET_WIDTH });
});
afterEach(() => session.getState().reset());

describe('AI chat widget', () => {
  it('preserves one live conversation and its draft across minimize, navigation and full-page expansion', async () => {
    let handlers!: KnowledgeStreamHandlers;
    let finish!: () => void;
    vi.mocked(askStream).mockImplementation((_request, callbacks) => {
      handlers = callbacks;
      return new Promise<void>((resolve) => { finish = resolve; });
    });
    renderApp();
    fireEvent.click(await screen.findByRole('button', { name: 'Open NodePilot Assistant' }));
    const dialog = await screen.findByRole('dialog');
    const input = await within(dialog).findByRole('textbox');
    fireEvent.change(input, { target: { value: 'Explain NodePilot' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(askStream).toHaveBeenCalledTimes(1));
    act(() => handlers.onDelta('Partial answer'));
    fireEvent.change(input, { target: { value: 'Follow-up draft' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Minimize assistant' }));
    expect(handlers.signal?.aborted).toBe(false);
    fireEvent.click(screen.getByRole('link', { name: 'Workflows' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open NodePilot Assistant' }));
    expect(await screen.findByRole('textbox')).toHaveValue('Follow-up draft');
    expect(screen.getByText('Partial answer')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open full chat' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(await screen.findByRole('textbox')).toHaveValue('Follow-up draft');
    expect(screen.getByRole('button', { name: /stop/i })).toBeInTheDocument();
    await act(async () => { handlers.onDelta(' completed'); finish(); });
    expect(screen.getByText('Partial answer completed')).toBeInTheDocument();
    expect(askStream).toHaveBeenCalledTimes(1);
  });

  it('shows only the sources available to this user and three starter questions', async () => {
    renderApp();
    fireEvent.click(await screen.findByRole('button', { name: 'Open NodePilot Assistant' }));
    const dialog = await screen.findByRole('dialog');
    const empty = await within(dialog).findByTestId('ai-chat-empty');
    expect(within(empty).getAllByRole('button')).toHaveLength(3);
    fireEvent.click(within(dialog).getByText('Available knowledge sources'));
    expect(within(dialog).getByText(/^Docs$/)).toBeVisible();
    expect(within(dialog).queryByText(/^Database$/)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/^Source code$/)).not.toBeInTheDocument();
  });

  it('closes the thread menu before minimizing on Escape', async () => {
    renderApp();
    fireEvent.click(await screen.findByRole('button', { name: 'Open NodePilot Assistant' }));
    const threadMenu = await screen.findByRole('button', { name: /chats/i });
    fireEvent.click(threadMenu);
    fireEvent.keyDown(threadMenu, { key: 'Escape' });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(threadMenu).toHaveAttribute('aria-expanded', 'false');
    fireEvent.keyDown(threadMenu, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('hides the launcher when the global chat is disabled', async () => {
    vi.mocked(getKnowledgeCapabilities).mockResolvedValue({ enabled: false, llm: true, docs: false, operational: false, db: false, sourceCode: false });
    renderApp();
    await waitFor(() => expect(getKnowledgeCapabilities).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: /NodePilot Assistant/ })).not.toBeInTheDocument();
  });

  it('hides the launcher in the workflow designer, which brings its own assistant', async () => {
    renderApp();
    // The workflow list keeps the launcher; only the editor route replaces it.
    fireEvent.click(screen.getByRole('link', { name: 'Workflows' }));
    expect(await screen.findByRole('button', { name: /NodePilot Assistant/ })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Open designer' }));
    expect(screen.getByText('Designer')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /NodePilot Assistant/ })).not.toBeInTheDocument();
  });

  it('widens the panel when its handle is dragged left and remembers the width', async () => {
    renderApp();
    fireEvent.click(await screen.findByRole('button', { name: /NodePilot Assistant/ }));
    const panel = screen.getByRole('dialog');

    // The panel is anchored bottom-right, so only the left edge moves: dragging it left by 80
    // widens the panel by 80. Every element measures 600px wide in the test harness, which is
    // what the drag seeds from.
    fireEvent.mouseDown(screen.getByTestId('ai-chat-widget-resize'), { clientX: 500 });
    fireEvent.mouseMove(document, { clientX: 420 });
    fireEvent.mouseUp(document);

    expect(panel.style.getPropertyValue('--np-chat-widget-width')).toBe('680px');
    expect(useChatLayoutStore.getState().widgetWidth).toBe(680);

    // Double-click restores the original panel width.
    fireEvent.doubleClick(screen.getByTestId('ai-chat-widget-resize'));
    expect(panel.style.getPropertyValue('--np-chat-widget-width')).toBe(`${DEFAULT_WIDGET_WIDTH}px`);
    expect(useChatLayoutStore.getState().widgetWidth).toBe(DEFAULT_WIDGET_WIDTH);
  });
});
