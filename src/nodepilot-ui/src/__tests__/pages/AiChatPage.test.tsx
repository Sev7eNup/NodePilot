import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AiChatPage } from '../../pages/AiChatPage';
import {
  askStream, getKnowledgeCapabilities,
  type KnowledgeAskRequest, type KnowledgeStreamHandlers, type ChatDoneMeta,
} from '../../api/ai';
import { useAiChatStore } from '../../stores/aiChatStore';
import { useAuthStore } from '../../stores/authStore';

vi.mock('../../api/ai', async (orig) => {
  const actual = await orig<typeof import('../../api/ai')>();
  return { ...actual, askStream: vi.fn(), getKnowledgeCapabilities: vi.fn() };
});

const askMock = askStream as unknown as ReturnType<typeof vi.fn>;
const capsMock = getKnowledgeCapabilities as unknown as ReturnType<typeof vi.fn>;

// The `if (!h) return` guard mirrors the designer test: vitest's mock-spread invokes the impl a
// second time under act() without handlers; the real component calls askStream once.
function streamMock(opts: { reply: string; meta?: ChatDoneMeta }) {
  return (_req: KnowledgeAskRequest, h?: KnowledgeStreamHandlers) => {
    if (!h) return Promise.resolve();
    h.onDelta(opts.reply);
    if (opts.meta) h.onDone?.(opts.meta);
    return Promise.resolve();
  };
}

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AiChatPage />
    </QueryClientProvider>,
  );
}

async function ask(question: string) {
  fireEvent.change(screen.getByRole('textbox'), { target: { value: question } });
  fireEvent.click(screen.getByRole('button', { name: /send/i }));
}

beforeEach(() => {
  askMock.mockReset();
  capsMock.mockReset();
  capsMock.mockResolvedValue({ enabled: true, docs: true, operational: true, sourceCode: false, db: false });
  // History lives in a module-global store — clear it so threads don't leak between tests.
  useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
  useAuthStore.setState({ userId: null });
});

describe('AiChatPage', () => {
  it('renders the header and (once loaded) the active source badges', async () => {
    renderPage();
    expect(screen.getByRole('heading', { name: /ai chat/i })).toBeInTheDocument();
    // Docs + operational enabled → their badges appear; sourceCode is off → absent.
    await waitFor(() => expect(screen.getByText(/^docs$/i)).toBeInTheDocument());
    expect(screen.getByText(/workflows & operations/i)).toBeInTheDocument();
    expect(screen.queryByText(/^source code$/i)).not.toBeInTheDocument();
  });

  it('shows the disabled state when the chat is off', async () => {
    capsMock.mockResolvedValue({ enabled: false, docs: false, operational: false, sourceCode: false, db: false });
    renderPage();
    expect(await screen.findByText(/ai chat is disabled/i)).toBeInTheDocument();
  });

  it('streams a question and renders the markdown reply', async () => {
    askMock.mockImplementation(streamMock({ reply: 'NodePilot is a **workflow orchestrator**.' }));
    renderPage();

    await ask('What is NodePilot?');

    await waitFor(() => expect(screen.getByText(/workflow orchestrator/i)).toBeInTheDocument());
    const req = askMock.mock.calls[0][0] as KnowledgeAskRequest;
    expect(req.question).toBe('What is NodePilot?');
    // The typed question also shows as a user bubble.
    expect(screen.getByText('What is NodePilot?')).toBeInTheDocument();
  });

  it('renders read-only tool activity when the assistant calls a tool', async () => {
    askMock.mockImplementation((_req: KnowledgeAskRequest, h?: KnowledgeStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onToolCall?.('search_docs', 'c1');
      h.onToolResult?.('search_docs', 'c1');
      h.onDelta('Found it.');
      return Promise.resolve();
    });
    renderPage();

    await ask('How do triggers work?');

    await waitFor(() => expect(screen.getByText('search_docs')).toBeInTheDocument());
    expect(screen.getByText(/checked/i)).toBeInTheDocument();
  });

  it('sends on Enter and inserts a newline on Shift+Enter', async () => {
    askMock.mockImplementation(streamMock({ reply: 'ok' }));
    renderPage();

    const box = screen.getByRole('textbox');
    fireEvent.change(box, { target: { value: 'a question' } });
    fireEvent.keyDown(box, { key: 'Enter', shiftKey: true });
    expect(askMock).not.toHaveBeenCalled();

    fireEvent.keyDown(box, { key: 'Enter' });
    await waitFor(() => expect(askMock).toHaveBeenCalledTimes(1));
    expect((askMock.mock.calls[0][0] as KnowledgeAskRequest).question).toBe('a question');
  });

  it('regenerates the last answer (re-asks the same question, replaces the bubble)', async () => {
    askMock.mockImplementation(streamMock({ reply: 'first answer' }));
    renderPage();
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/first answer/i)).toBeInTheDocument());

    askMock.mockImplementation(streamMock({ reply: 'second answer' }));
    fireEvent.click(screen.getByRole('button', { name: /regenerate/i }));

    await waitFor(() => expect(screen.getByText(/second answer/i)).toBeInTheDocument());
    expect(askMock).toHaveBeenCalledTimes(2);
    expect((askMock.mock.calls[1][0] as KnowledgeAskRequest).question).toBe('explain');
    expect(screen.queryByText(/first answer/i)).not.toBeInTheDocument();
  });

  it('shows an error banner and retries from it', async () => {
    askMock.mockImplementation((_req: KnowledgeAskRequest, h?: KnowledgeStreamHandlers) =>
      h ? Promise.reject(new Error('LLM endpoint unreachable')) : Promise.resolve());
    renderPage();
    await ask('explain');
    expect(await screen.findByRole('alert')).toHaveTextContent(/unreachable/i);

    askMock.mockImplementation(streamMock({ reply: 'recovered' }));
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    await waitFor(() => expect(screen.getByText(/recovered/i)).toBeInTheDocument());
    expect(askMock).toHaveBeenCalledTimes(2);
  });

  it('does NOT show an error when the user stops the stream (AbortError)', async () => {
    const abortErr = new DOMException('aborted', 'AbortError');
    askMock.mockImplementation((_req: KnowledgeAskRequest, h?: KnowledgeStreamHandlers) => {
      if (!h) return Promise.resolve();
      h.onDelta('partial answer');
      return Promise.reject(abortErr);
    });
    renderPage();

    await ask('explain');
    await waitFor(() => expect(screen.getByText(/partial answer/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('renders the usage footer (model + tokens + tok/s) after the stream completes', async () => {
    askMock.mockImplementation(streamMock({
      reply: 'done answer',
      meta: { model: 'gpt-x', durationMs: 1000, promptTokens: 100, completionTokens: 50 },
    }));
    renderPage();
    await ask('explain');

    await waitFor(() => expect(screen.getByText(/gpt-x/i)).toBeInTheDocument());
    expect(screen.getByText(/150 tokens/i)).toBeInTheDocument();
    expect(screen.getByText(/50 tok\/s/i)).toBeInTheDocument();
  });

  it('sends an example prompt from the empty state', async () => {
    askMock.mockImplementation(streamMock({ reply: 'ok' }));
    renderPage();

    // The first example is the webhook-trigger prompt.
    fireEvent.click(screen.getByRole('button', { name: /webhook trigger/i }));

    await waitFor(() => expect(askMock).toHaveBeenCalledTimes(1));
    expect((askMock.mock.calls[0][0] as KnowledgeAskRequest).question).toMatch(/webhook trigger/i);
  });

  it('isolates threads — "New chat" starts an empty thread', async () => {
    askMock.mockImplementation(streamMock({ reply: 'first answer' }));
    renderPage();

    await ask('first question');
    await waitFor(() => expect(screen.getByText('first answer')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /chats/i }));
    fireEvent.click(screen.getByText(/new chat/i));

    await waitFor(() => expect(screen.queryByText('first answer')).not.toBeInTheDocument());
  });

  it('persists the thread across remount (store-backed history)', async () => {
    askMock.mockImplementation(streamMock({ reply: 'remembered' }));
    const { unmount } = renderPage();
    await ask('explain');
    await waitFor(() => expect(screen.getByText(/remembered/i)).toBeInTheDocument());

    unmount();
    renderPage();
    expect(screen.getByText(/remembered/i)).toBeInTheDocument();
  });
});
