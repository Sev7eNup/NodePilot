import { create } from 'zustand';
import { askStream, type AiChatTurn } from '../api/ai';
import {
  addToolCallToLast, appendToLastAssistant, finalizeStreaming, isAbort,
  markToolDoneOnLast, patchLastAssistant, trimHistory,
} from '../lib/chatMessages';
import {
  captureAuthBoundaryGeneration, isAuthBoundaryGenerationCurrent,
  registerAuthBoundaryLiveStateClearer,
} from '../security/authBoundary';
import { aiChatFullKey, useAiChatStore, type ChatMessage } from './aiChatStore';

interface KnowledgeChatSession {
  widgetOpen: boolean;
  workflowChatOpen: boolean;
  pageVisible: boolean;
  unread: boolean;
  sendingKey: string | null;
  drafts: Record<string, string>;
  errors: Record<string, string | null>;
  setWidgetOpen: (open: boolean) => void;
  setWorkflowChatOpen: (open: boolean) => void;
  setPageVisible: (visible: boolean) => void;
  setDraft: (key: string, value: string) => void;
  clearError: (key: string) => void;
  forgetThread: (key: string) => void;
  send: (scope: string, threadId: string, question: string) => Promise<void>;
  regenerate: (scope: string, threadId: string) => Promise<void>;
  stop: () => void;
  reset: () => void;
}

const initialState = {
  widgetOpen: false, workflowChatOpen: false, pageVisible: false, unread: false,
  sendingKey: null, drafts: {}, errors: {},
};

type Request = { controller: AbortController; finish: () => void };
let activeRequest: Request | null = null;

// Request ownership lives outside the view so minimizing and route changes keep the stream alive.
async function stream(scope: string, threadId: string, question: string, history: AiChatTurn[]) {
  const key = aiChatFullKey(scope, threadId);
  const generation = captureAuthBoundaryGeneration();
  const controller = new AbortController();
  const request: Request = { controller, finish: () => update(finalizeStreaming) };
  activeRequest = request;
  const current = () => activeRequest === request && isAuthBoundaryGenerationCurrent(generation);
  const update = (updater: (messages: ChatMessage[]) => ChatMessage[]) => {
    if (current()) useAiChatStore.getState().updateMessages(scope, threadId, updater);
  };
  useKnowledgeChatSessionStore.setState((s) => ({ sendingKey: key, errors: { ...s.errors, [key]: null }, unread: false }));
  update((messages) => [...messages, { role: 'assistant', content: '', streaming: true }]);
  let completed = false;
  try {
    await askStream({
      question, history: trimHistory(history),
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      utcOffsetMinutes: -new Date().getTimezoneOffset(),
    }, {
      signal: controller.signal,
      onDelta: (text) => update((messages) => appendToLastAssistant(messages, text)),
      onToolCall: (name, id) => update((messages) => addToolCallToLast(messages, id, name)),
      onToolResult: (_name, id) => update((messages) => markToolDoneOnLast(messages, id)),
      onDone: (meta) => update((messages) => patchLastAssistant(messages, { meta })),
    });
    completed = true;
  } catch (error) {
    if (current() && !isAbort(error)) {
      useKnowledgeChatSessionStore.setState((s) => ({
        errors: { ...s.errors, [key]: error instanceof Error ? error.message : String(error) },
      }));
    }
  } finally {
    if (current()) {
      request.finish();
      activeRequest = null;
      useKnowledgeChatSessionStore.setState((s) => ({
        sendingKey: null, unread: completed && !s.widgetOpen && !s.pageVisible,
      }));
    }
  }
}

export const useKnowledgeChatSessionStore = create<KnowledgeChatSession>((set, get) => ({
  ...initialState,
  setWidgetOpen: (widgetOpen) => set((s) => ({
    widgetOpen, workflowChatOpen: widgetOpen ? false : s.workflowChatOpen,
    unread: widgetOpen ? false : s.unread,
  })),
  setWorkflowChatOpen: (workflowChatOpen) => set((s) => ({
    workflowChatOpen, widgetOpen: workflowChatOpen ? false : s.widgetOpen,
  })),
  setPageVisible: (pageVisible) => set((s) => ({
    pageVisible, widgetOpen: pageVisible ? false : s.widgetOpen, unread: pageVisible ? false : s.unread,
  })),
  setDraft: (key, value) => set((s) => ({ drafts: { ...s.drafts, [key]: value } })),
  clearError: (key) => set((s) => ({ errors: { ...s.errors, [key]: null } })),
  forgetThread: (key) => set((s) => {
    const drafts = { ...s.drafts };
    const errors = { ...s.errors };
    delete drafts[key];
    delete errors[key];
    return { drafts, errors };
  }),
  send: async (scope, threadId, raw) => {
    const question = raw.trim();
    if (!question || !threadId || activeRequest) return;
    const key = aiChatFullKey(scope, threadId);
    const history = useAiChatStore.getState().messagesByThread[key] ?? [];
    get().setDraft(key, '');
    useAiChatStore.getState().updateMessages(scope, threadId, (messages) => [...messages, { role: 'user', content: question }]);
    await stream(scope, threadId, question, history.map(({ role, content }) => ({ role, content })));
  },
  regenerate: async (scope, threadId) => {
    if (!threadId || activeRequest) return;
    const messages = useAiChatStore.getState().messagesByThread[aiChatFullKey(scope, threadId)] ?? [];
    const index = messages.findLastIndex((message) => message.role === 'user');
    if (index < 0) return;
    const question = messages[index].content;
    useAiChatStore.getState().updateMessages(scope, threadId, () => messages.slice(0, index + 1));
    await stream(scope, threadId, question, messages.slice(0, index).map(({ role, content }) => ({ role, content })));
  },
  stop: () => {
    const request = activeRequest;
    if (!request) return;
    request.finish();
    activeRequest = null;
    request.controller.abort();
    set({ sendingKey: null });
  },
  reset: () => {
    const request = activeRequest;
    activeRequest = null;
    request?.controller.abort();
    set(initialState);
  },
}));

registerAuthBoundaryLiveStateClearer(() => useKnowledgeChatSessionStore.getState().reset());
