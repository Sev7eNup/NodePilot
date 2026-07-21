import { describe, it, expect, beforeEach } from 'vitest';
import { useAiChatStore, aiChatScopeKey, aiChatFullKey, type ChatMessage } from '../../stores/aiChatStore';

const STORAGE_KEY = 'nodepilot-aichat';

function reset() {
  useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
  localStorage.removeItem(STORAGE_KEY);
}

/** Reads the partialized state that the persist middleware wrote to localStorage. */
function persisted(): {
  messagesByThread: Record<string, ChatMessage[]>;
  threadsByScope: Record<string, unknown[]>;
  activeThreadByScope: Record<string, string>;
} {
  const raw = localStorage.getItem(STORAGE_KEY);
  return raw ? JSON.parse(raw).state : { messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} };
}

describe('aiChatStore', () => {
  beforeEach(reset);

  describe('thread management', () => {
    it('ensureActiveThread creates a default thread when none exists, and is idempotent', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().ensureActiveThread(scope, 'Chat 1');
      const again = useAiChatStore.getState().ensureActiveThread(scope, 'Chat 1');

      const s = useAiChatStore.getState();
      expect(again).toBe(id);
      expect(s.threadsByScope[scope]).toHaveLength(1);
      expect(s.threadsByScope[scope][0].name).toBe('Chat 1');
      expect(s.activeThreadByScope[scope]).toBe(id);
    });

    it('newThread adds a thread and makes it active', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const a = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const b = useAiChatStore.getState().newThread(scope, 'Chat 2');

      const s = useAiChatStore.getState();
      expect(s.threadsByScope[scope].map((t) => t.name)).toEqual(['Chat 1', 'Chat 2']);
      expect(s.activeThreadByScope[scope]).toBe(b);
      expect(a).not.toBe(b);
    });

    it('renameThread updates the name', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      useAiChatStore.getState().renameThread(scope, id, 'Disk check');
      expect(useAiChatStore.getState().threadsByScope[scope][0].name).toBe('Disk check');
    });

    it('deleteThread drops the thread + its messages and reactivates the last remaining', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const a = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const b = useAiChatStore.getState().newThread(scope, 'Chat 2');
      useAiChatStore.getState().updateMessages(scope, b, () => [{ role: 'user', content: 'hi' }]);

      useAiChatStore.getState().deleteThread(scope, b);

      const s = useAiChatStore.getState();
      expect(s.threadsByScope[scope].map((t) => t.id)).toEqual([a]);
      expect(s.activeThreadByScope[scope]).toBe(a); // active fell back to the remaining thread
      expect(s.messagesByThread[aiChatFullKey(scope, b)]).toBeUndefined();
    });

    it('updateMessages writes messages and bumps the thread updatedAt', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const before = useAiChatStore.getState().threadsByScope[scope][0].updatedAt;

      useAiChatStore.getState().updateMessages(scope, id, () => [{ role: 'user', content: 'x' }]);

      const s = useAiChatStore.getState();
      expect(s.messagesByThread[aiChatFullKey(scope, id)]).toHaveLength(1);
      expect(s.threadsByScope[scope][0].updatedAt).toBeGreaterThanOrEqual(before);
    });

    it('clearAll empties every map (logout)', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      useAiChatStore.getState().updateMessages(scope, id, () => [{ role: 'user', content: 'x' }]);

      useAiChatStore.getState().clearAll();

      const s = useAiChatStore.getState();
      expect(s.threadsByScope).toEqual({});
      expect(s.messagesByThread).toEqual({});
      expect(s.activeThreadByScope).toEqual({});
    });

    it('scopes are isolated per user and per workflow', () => {
      const u1wf1 = aiChatScopeKey('u1', 'wf1');
      const u2wf1 = aiChatScopeKey('u2', 'wf1');
      useAiChatStore.getState().newThread(u1wf1, 'Chat 1');
      expect(useAiChatStore.getState().threadsByScope[u2wf1]).toBeUndefined();
    });
  });

  describe('persistence (partialize)', () => {
    it('strips baseDef and streaming/building flags but keeps proposal.definitionJson', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const msg: ChatMessage = {
        role: 'assistant', content: 'done', streaming: true, building: true,
        baseDef: { nodes: [], edges: [] },
        proposal: { definitionJson: '{"nodes":[]}', summary: 's', nodeCount: 1, edgeCount: 0, baseDefinitionHash: 'h' },
      };
      useAiChatStore.getState().updateMessages(scope, id, () => [msg]);

      const stored = persisted().messagesByThread[aiChatFullKey(scope, id)][0];
      expect(stored.baseDef).toBeUndefined();
      expect(stored.streaming).toBeUndefined();
      expect(stored.building).toBeUndefined();
      expect(stored.content).toBe('done'); // prose survives
      expect(stored.proposal?.definitionJson).toBe('{"nodes":[]}'); // survives reload → stays applicable
      expect(stored.proposal?.summary).toBe('s');
    });

    it('degrades an oversized proposal.definitionJson to the empty stub', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const huge = `{"nodes":["${'x'.repeat(150_000)}"]}`;
      const msg: ChatMessage = {
        role: 'assistant', content: 'done',
        proposal: { definitionJson: huge, summary: 's', nodeCount: 1, edgeCount: 0, baseDefinitionHash: 'h' },
      };
      useAiChatStore.getState().updateMessages(scope, id, () => [msg]);

      const stored = persisted().messagesByThread[aiChatFullKey(scope, id)][0];
      expect(stored.proposal?.definitionJson).toBe(''); // over the cap → read-only stub
      expect(stored.proposal?.summary).toBe('s');
    });

    it('keeps definitionJson only for the newest proposal in a thread (aggregate bound)', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const proposalMsg = (json: string): ChatMessage => ({
        role: 'assistant', content: 'done',
        proposal: { definitionJson: json, summary: 's', nodeCount: 1, edgeCount: 0, baseDefinitionHash: 'h' },
      });
      useAiChatStore.getState().updateMessages(scope, id, () => [
        proposalMsg('{"old":true}'),
        { role: 'user', content: 'refine it' },
        proposalMsg('{"new":true}'),
      ]);

      const stored = persisted().messagesByThread[aiChatFullKey(scope, id)];
      expect(stored[0].proposal?.definitionJson).toBe('');            // superseded → stub
      expect(stored[0].proposal?.summary).toBe('s');                  // metadata survives
      expect(stored[2].proposal?.definitionJson).toBe('{"new":true}'); // newest stays applicable
    });

    it('does NOT persist threads of unsaved (__new__) workflows', () => {
      const newScope = aiChatScopeKey('u1', undefined); // → u1::__new__
      const id = useAiChatStore.getState().newThread(newScope, 'Chat 1');
      useAiChatStore.getState().updateMessages(newScope, id, () => [{ role: 'user', content: 'hi' }]);

      expect(useAiChatStore.getState().threadsByScope[newScope]).toHaveLength(1); // live state has it
      const p = persisted();
      expect(p.threadsByScope[newScope]).toBeUndefined();                         // persisted does not
      expect(p.messagesByThread[aiChatFullKey(newScope, id)]).toBeUndefined();
    });

    it('caps persisted messages per thread to the last 200', () => {
      const scope = aiChatScopeKey('u1', 'wf1');
      const id = useAiChatStore.getState().newThread(scope, 'Chat 1');
      const many: ChatMessage[] = Array.from({ length: 250 }, (_, i) => ({ role: 'user', content: `m${i}` }));
      useAiChatStore.getState().updateMessages(scope, id, () => many);

      expect(useAiChatStore.getState().messagesByThread[aiChatFullKey(scope, id)]).toHaveLength(250); // live keeps all
      const stored = persisted().messagesByThread[aiChatFullKey(scope, id)];
      expect(stored).toHaveLength(200);
      expect(stored[stored.length - 1].content).toBe('m249'); // newest retained
    });
  });
});
