import type { AiChatTurn } from '../api/ai';
import type { ChatMessage } from '../stores/aiChatStore';

// The backend caps history at 20 turns and 50k characters (AiChatController,
// AiKnowledgeController). Staying below those caps here keeps long threads from getting a
// 400 HISTORY_TOO_LONG response.
const MAX_HISTORY_TURNS = 19;
const MAX_HISTORY_CHARS = 48_000;

/** True for a user-initiated stream abort: the partial bubble stays and no error is shown. */
export function isAbort(err: unknown): boolean {
  return (err instanceof DOMException || err instanceof Error) && err.name === 'AbortError';
}

/** Trims the history to the backend caps: the most recent turns within the character limit. */
export function trimHistory(history: AiChatTurn[]): AiChatTurn[] {
  let turns = history.slice(-MAX_HISTORY_TURNS);
  let total = turns.reduce((s, m) => s + m.content.length, 0);
  while (turns.length > 0 && total > MAX_HISTORY_CHARS) {
    total -= turns[0].content.length;
    turns = turns.slice(1);
  }
  return turns;
}

/** The history context key of the most recent answer, or null when no answer carries one. */
export function lastHistoryContextKey(messages: ChatMessage[]): string | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    const key = messages[i].role === 'assistant' ? messages[i].meta?.historyContextKey : null;
    if (key) return key;
  }
  return null;
}

/** Keep displayed history, but never replay messages preceding the latest recipient boundary.
 * The server's new key authorizes only the conversation begun after it dropped older history. */
export function contextBoundHistory(messages: ChatMessage[]): ChatMessage[] {
  const boundary = messages.findLastIndex((message) => message.role === 'assistant' && message.meta?.historyDropped);
  if (boundary < 0) return messages;
  const question = messages.slice(0, boundary).findLastIndex((message) => message.role === 'user');
  return messages.slice(question >= 0 ? question : boundary);
}

/** Appends text to the last assistant message (immutably). */
export function appendToLastAssistant(prev: ChatMessage[], text: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], content: next[i].content + text }; break; }
  }
  return next;
}

/** Patches the last assistant message (building/proposal/meta) immutably. */
export function patchLastAssistant(prev: ChatMessage[], patch: Partial<ChatMessage>): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], ...patch }; break; }
  }
  return next;
}

/** Appends an in-progress tool call to the last assistant message. */
export function addToolCallToLast(prev: ChatMessage[], toolId: string, toolName: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') {
      next[i] = { ...next[i], toolCalls: [...(next[i].toolCalls ?? []), { toolId, toolName, done: false }] };
      break;
    }
  }
  return next;
}

/** Marks a tool call on the last assistant message as completed. */
export function markToolDoneOnLast(prev: ChatMessage[], toolId: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') {
      next[i] = { ...next[i], toolCalls: (next[i].toolCalls ?? []).map((tc) => (tc.toolId === toolId ? { ...tc, done: true } : tc)) };
      break;
    }
  }
  return next;
}

/**
 * Clears the streaming and building flags on every assistant message. Only the designer panel
 * sets `building` (proposal buffering), so clearing it elsewhere is inert.
 */
export function finalizeStreaming(prev: ChatMessage[]): ChatMessage[] {
  return prev.map((m) => (m.streaming || m.building ? { ...m, streaming: false, building: false } : m));
}
