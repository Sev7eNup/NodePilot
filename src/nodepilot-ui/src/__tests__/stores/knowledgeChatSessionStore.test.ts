import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { askStream, type KnowledgeStreamHandlers } from '../../api/ai';
import { aiChatFullKey, useAiChatStore } from '../../stores/aiChatStore';
import { useKnowledgeChatSessionStore as session } from '../../stores/knowledgeChatSessionStore';
import { clearLocalAuthBoundary } from '../../security/authBoundary';
import '../../stores/authStore';

vi.mock('../../api/ai', async (original) => ({
  ...await original<typeof import('../../api/ai')>(),
  askStream: vi.fn(),
}));

const scope = 'user::global';
let thread: string;
let key: string;
let handlers: KnowledgeStreamHandlers;
let resolve: () => void;
let reject: (error: Error) => void;

beforeEach(() => {
  session.getState().reset();
  useAiChatStore.getState().clearAll();
  thread = useAiChatStore.getState().ensureActiveThread(scope, 'Chat 1');
  key = aiChatFullKey(scope, thread);
  vi.mocked(askStream).mockReset();
  vi.mocked(askStream).mockImplementation((_request, callbacks) => {
    handlers = callbacks;
    return new Promise<void>((done, fail) => { resolve = done; reject = fail; });
  });
});
afterEach(() => session.getState().reset());

describe('global chat request ownership', () => {
  it('keeps streaming while minimized and marks the completed reply unread', async () => {
    session.getState().setWidgetOpen(true);
    const pending = session.getState().send(scope, thread, 'Explain triggers');
    handlers.onDelta('First');
    session.getState().setWidgetOpen(false);
    handlers.onDelta(' answer');
    resolve();
    await pending;
    expect(useAiChatStore.getState().messagesByThread[key].at(-1)).toMatchObject({ content: 'First answer', streaming: false });
    expect(session.getState().unread).toBe(true);
    session.getState().setWidgetOpen(true);
    expect(session.getState().unread).toBe(false);
  });

  it('accepts only one request and keeps the draft written during generation', async () => {
    const pending = session.getState().send(scope, thread, 'First');
    session.getState().setDraft(key, 'Next question');
    await session.getState().send(scope, thread, 'Duplicate');
    expect(askStream).toHaveBeenCalledTimes(1);
    expect(session.getState().drafts[key]).toBe('Next question');
    resolve();
    await pending;
  });

  it('stops immediately and ignores late callbacks from the old request', async () => {
    const first = session.getState().send(scope, thread, 'First');
    const oldHandlers = handlers;
    const finishOld = resolve;
    handlers.onDelta('Partial');
    session.getState().stop();
    expect(oldHandlers.signal?.aborted).toBe(true);
    expect(session.getState().sendingKey).toBeNull();
    const second = session.getState().send(scope, thread, 'Second');
    handlers.onDelta('Current');
    oldHandlers.onDelta('Must not appear');
    finishOld();
    await first;
    expect(session.getState().sendingKey).toBe(key);
    resolve();
    await second;
    expect(useAiChatStore.getState().messagesByThread[key].map((m) => m.content)).toEqual(['First', 'Partial', 'Second', 'Current']);
  });

  it('aborts and clears sensitive state at logout without resurrecting history', async () => {
    const pending = session.getState().send(scope, thread, 'Private question');
    session.getState().setDraft(key, 'Private draft');
    handlers.onDelta('Private answer');
    clearLocalAuthBoundary();
    expect(handlers.signal?.aborted).toBe(true);
    handlers.onDelta('Late answer');
    reject(new Error('Late error'));
    await pending;
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(session.getState()).toMatchObject({ drafts: {}, errors: {}, sendingKey: null, widgetOpen: false, unread: false });
  });

  it('retries the failed turn with preceding history and no duplicate question', async () => {
    const pending = session.getState().send(scope, thread, 'Explain');
    reject(new Error('Unavailable'));
    await pending;
    expect(session.getState().errors[key]).toBe('Unavailable');
    const retry = session.getState().regenerate(scope, thread);
    handlers.onDelta('Recovered');
    resolve();
    await retry;
    expect(vi.mocked(askStream).mock.calls[1][0]).toMatchObject({ question: 'Explain', history: [] });
    expect(useAiChatStore.getState().messagesByThread[key].map((m) => m.content)).toEqual(['Explain', 'Recovered']);
    expect(session.getState().errors[key]).toBeNull();
  });

  it('writes to the originating thread even when the active thread changes', async () => {
    const pending = session.getState().send(scope, thread, 'Original');
    const other = useAiChatStore.getState().newThread(scope, 'Chat 2');
    handlers.onDelta('Original answer');
    resolve();
    await pending;
    expect(useAiChatStore.getState().messagesByThread[key].at(-1)?.content).toBe('Original answer');
    expect(useAiChatStore.getState().messagesByThread[aiChatFullKey(scope, other)] ?? []).toEqual([]);
  });

  it('coordinates the designer panel and clears unread state on the full page', () => {
    session.getState().setWorkflowChatOpen(true);
    session.getState().setWidgetOpen(true);
    expect(session.getState().workflowChatOpen).toBe(false);
    session.getState().setWorkflowChatOpen(true);
    expect(session.getState().widgetOpen).toBe(false);
    session.setState({ unread: true, widgetOpen: true });
    session.getState().setPageVisible(true);
    expect(session.getState()).toMatchObject({ pageVisible: true, unread: false, widgetOpen: false });
  });
});
