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
          <Routes>
            <Route path="/" element={<p>Dashboard</p>} />
            <Route path="/workflows" element={<p>Workflow list</p>} />
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
});
