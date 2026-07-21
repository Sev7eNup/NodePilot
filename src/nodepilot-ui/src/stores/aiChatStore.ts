import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { WorkflowChatProposal, ChatDoneMeta } from '../api/ai';
import type { WorkflowDefinition } from '../lib/workflowDiff';

/** One message in the workflow-assistant chat. */
export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  /** true while the assistant's bubble is still streaming in (shows a blinking cursor). */
  streaming?: boolean;
  /** true while the workflow definition is being generated/buffered after the prose reply. */
  building?: boolean;
  /** Only present on assistant turns where a change was requested and is allowed. */
  proposal?: WorkflowChatProposal;
  /** Canvas state at the time the question was asked — the basis for the diff preview. */
  baseDef?: WorkflowDefinition;
  /** Completion metadata (model / duration / tokens) for the usage footer. */
  meta?: ChatDoneMeta;
  /** Read-only tools the assistant called during this turn (e.g. `analyze_workflow`) — drives
   *  the "calling tool X..." indicator and gives the user visibility into what happened. */
  toolCalls?: ChatToolActivity[];
}

/** One tool call made by the assistant within a turn. `done=false` while the result is still pending. */
export interface ChatToolActivity {
  toolId: string;
  toolName: string;
  done: boolean;
}

/** Metadata for a named chat thread within a workflow's scope. */
export interface ChatThreadMeta {
  id: string;
  name: string;
  createdAt: number;
  updatedAt: number;
}

/** Cap on persisted messages per thread (older ones are dropped when saving). */
const MAX_PERSISTED_MESSAGES = 200;

/**
 * Cap on the size of a persisted `proposal.definitionJson`. Below the cap, a proposal
 * survives a page reload and stays applicable (the diff base is reconstructed from the
 * live canvas as long as its hash still matches `baseDefinitionHash`); above the cap it
 * degrades to a read-only "expired" notice, so that a pathologically large workflow
 * definition can't blow out the localStorage quota.
 */
const MAX_PERSISTED_PROPOSAL_CHARS = 100_000;

const EMPTY_MESSAGES: ChatMessage[] = [];

/**
 * Holds chat history **per user, workflow, and thread**. Unlike an earlier version, this
 * store is now `persist`-ed (survives a page reload), but privacy-conscious: `partialize`
 * strips the heavy/sensitive fields (`baseDef` snapshots, `proposal.definitionJson`,
 * streaming flags) and does **not** persist threads for unsaved workflows (`__new__`).
 * Logout calls `clearAll()`, which also empties the persisted store — so the next user on
 * this browser never sees someone else's chat history.
 *
 * Keys: `scope` = `userId::workflowId`; the full message key = `scope::threadId`.
 */
interface AiChatStore {
  messagesByThread: Record<string, ChatMessage[]>;
  threadsByScope: Record<string, ChatThreadMeta[]>;
  activeThreadByScope: Record<string, string>;

  /** Patches a thread's messages and stamps `updatedAt` on the thread metadata. */
  updateMessages: (scope: string, threadId: string, updater: (prev: ChatMessage[]) => ChatMessage[]) => void;
  /** Returns the active thread for the scope; creates a default thread if none exists yet. */
  ensureActiveThread: (scope: string, defaultName: string) => string;
  /** Creates a new thread, makes it active, and returns its ID. */
  newThread: (scope: string, name: string) => string;
  renameThread: (scope: string, threadId: string, name: string) => void;
  deleteThread: (scope: string, threadId: string) => void;
  setActiveThread: (scope: string, threadId: string) => void;
  clearAll: () => void;
}

export function aiChatScopeKey(userId: string | null, workflowId: string | undefined): string {
  return `${userId ?? 'anon'}::${workflowId ?? '__new__'}`;
}

export function aiChatFullKey(scope: string, threadId: string): string {
  return `${scope}::${threadId}`;
}

function makeThreadId(): string {
  return `t-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function makeThread(name: string): ChatThreadMeta {
  const now = Date.now();
  return { id: makeThreadId(), name, createdAt: now, updatedAt: now };
}

/** A scope is persistable when it doesn't belong to an unsaved workflow (`__new__`). */
function isPersistableScope(scopeKey: string): boolean {
  return scopeKey.split('::')[1] !== '__new__';
}

/**
 * Strips heavy/sensitive fields before persisting (the diff-base snapshot, streaming flags).
 * Proposal JSON survives a reload only for the thread's **most recent** proposal (and only
 * up to a size cap) so it stays applicable: older proposals are superseded anyway — the
 * apply path would reject them via the stale-hash guard — so they persist as an empty `''`
 * stub instead. Without this, 200 messages at ~90 KB each could blow out the localStorage
 * quota. The diff base itself needs no snapshot — the panel reconstructs it from the live
 * canvas as soon as its hash matches the proposal's baseDefinitionHash.
 */
function stripForPersist(messages: ChatMessage[]): ChatMessage[] {
  const kept = messages.slice(-MAX_PERSISTED_MESSAGES);
  const lastProposalIdx = kept.reduce((acc, m, i) => (m.proposal ? i : acc), -1);
  return kept.map((m, i) => {
    const { baseDef: _baseDef, streaming: _s, building: _b, proposal, ...rest } = m;
    const out: ChatMessage = { ...rest };
    if (proposal) {
      const keepJson = i === lastProposalIdx && proposal.definitionJson.length <= MAX_PERSISTED_PROPOSAL_CHARS;
      out.proposal = keepJson ? { ...proposal } : { ...proposal, definitionJson: '' };
    }
    return out;
  });
}

export const useAiChatStore = create<AiChatStore>()(
  persist(
    (set, get) => ({
      messagesByThread: {},
      threadsByScope: {},
      activeThreadByScope: {},

      updateMessages: (scope, threadId, updater) =>
        set((s) => {
          const key = aiChatFullKey(scope, threadId);
          const next = updater(s.messagesByThread[key] ?? []);
          const threads = (s.threadsByScope[scope] ?? []).map((th) =>
            th.id === threadId ? { ...th, updatedAt: Date.now() } : th);
          return {
            messagesByThread: { ...s.messagesByThread, [key]: next },
            threadsByScope: { ...s.threadsByScope, [scope]: threads },
          };
        }),

      ensureActiveThread: (scope, defaultName) => {
        const s = get();
        const threads = s.threadsByScope[scope] ?? [];
        const active = s.activeThreadByScope[scope];
        if (active && threads.some((th) => th.id === active)) return active;
        if (threads.length > 0) {
          const fallback = threads[threads.length - 1].id;
          set((st) => ({ activeThreadByScope: { ...st.activeThreadByScope, [scope]: fallback } }));
          return fallback;
        }
        const thread = makeThread(defaultName);
        set((st) => ({
          threadsByScope: { ...st.threadsByScope, [scope]: [thread] },
          activeThreadByScope: { ...st.activeThreadByScope, [scope]: thread.id },
        }));
        return thread.id;
      },

      newThread: (scope, name) => {
        const thread = makeThread(name);
        set((s) => ({
          threadsByScope: { ...s.threadsByScope, [scope]: [...(s.threadsByScope[scope] ?? []), thread] },
          activeThreadByScope: { ...s.activeThreadByScope, [scope]: thread.id },
        }));
        return thread.id;
      },

      renameThread: (scope, threadId, name) =>
        set((s) => ({
          threadsByScope: {
            ...s.threadsByScope,
            [scope]: (s.threadsByScope[scope] ?? []).map((th) =>
              th.id === threadId ? { ...th, name, updatedAt: Date.now() } : th),
          },
        })),

      deleteThread: (scope, threadId) =>
        set((s) => {
          const remaining = (s.threadsByScope[scope] ?? []).filter((th) => th.id !== threadId);
          const messages = { ...s.messagesByThread };
          delete messages[aiChatFullKey(scope, threadId)];
          const active = { ...s.activeThreadByScope };
          if (active[scope] === threadId) {
            if (remaining.length > 0) active[scope] = remaining[remaining.length - 1].id;
            else delete active[scope];
          }
          return {
            threadsByScope: { ...s.threadsByScope, [scope]: remaining },
            messagesByThread: messages,
            activeThreadByScope: active,
          };
        }),

      setActiveThread: (scope, threadId) =>
        set((s) => ({ activeThreadByScope: { ...s.activeThreadByScope, [scope]: threadId } })),

      clearAll: () => set({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} }),
    }),
    {
      name: 'nodepilot-aichat',
      version: 1,
      // Only persist saved workflows; strip sensitive/heavy fields.
      partialize: (state) => {
        const threadsByScope: Record<string, ChatThreadMeta[]> = {};
        const activeThreadByScope: Record<string, string> = {};
        for (const [scope, threads] of Object.entries(state.threadsByScope)) {
          if (!isPersistableScope(scope)) continue;
          threadsByScope[scope] = threads;
          if (state.activeThreadByScope[scope]) activeThreadByScope[scope] = state.activeThreadByScope[scope];
        }
        const messagesByThread: Record<string, ChatMessage[]> = {};
        for (const [key, messages] of Object.entries(state.messagesByThread)) {
          if (!isPersistableScope(key)) continue; // key starts with scope → same __new__ check applies
          messagesByThread[key] = stripForPersist(messages);
        }
        return { messagesByThread, threadsByScope, activeThreadByScope };
      },
    },
  ),
);

/** Stable empty reference for selectors (avoids re-render loops). */
export const aiChatEmptyMessages = EMPTY_MESSAGES;
