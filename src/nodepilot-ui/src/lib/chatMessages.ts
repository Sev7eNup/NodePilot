import type { AiChatTurn } from '../api/ai';
import type { ChatMessage } from '../stores/aiChatStore';

// The backend caps history at 20 turns / 50k characters (AiChatController, AiKnowledgeController)
// → trim hard here, otherwise long threads get a 400 HISTORY_TOO_LONG response.
const MAX_HISTORY_TURNS = 19;
const MAX_HISTORY_CHARS = 48_000;

/** True for a user-initiated stream abort — the partial bubble stays and no error is shown. */
export function isAbort(err: unknown): boolean {
  return (err instanceof DOMException || err instanceof Error) && err.name === 'AbortError';
}

/** Trims the history sent to the backend down to its caps (most recent turns, ≤ character limit). */
export function trimHistory(history: AiChatTurn[]): AiChatTurn[] {
  let turns = history.slice(-MAX_HISTORY_TURNS);
  let total = turns.reduce((s, m) => s + m.content.length, 0);
  while (turns.length > 0 && total > MAX_HISTORY_CHARS) {
    total -= turns[0].content.length;
    turns = turns.slice(1);
  }
  return turns;
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
 * Marks all assistant messages as done (streaming/building=false). `building` only ever gets set
 * by the designer panel (proposal buffering); for chats that never set it the extra clear is inert
 * — and the store strips both flags before persisting anyway.
 */
export function finalizeStreaming(prev: ChatMessage[]): ChatMessage[] {
  return prev.map((m) => (m.streaming || m.building ? { ...m, streaming: false, building: false } : m));
}
