import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { chatSessionStorage, useChatPersistenceStatus } from '../../stores/chatPersistenceStatus';
import { useAiChatStore, aiChatScopeKey, aiChatFullKey } from '../../stores/aiChatStore';

const quotaExceeded = () => new DOMException('The quota has been exceeded.', 'QuotaExceededError');

describe('chat persistence status', () => {
  beforeEach(() => {
    sessionStorage.clear();
    useChatPersistenceStatus.setState({ unavailable: false, trimmed: false });
    useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
  });
  afterEach(() => vi.restoreAllMocks());

  it('keeps the chat usable in memory when sessionStorage rejects a write', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw quotaExceeded(); });
    const scope = aiChatScopeKey('u1', 'wf1');
    const thread = useAiChatStore.getState().newThread(scope, 'Chat 1');

    expect(() => useAiChatStore.getState().updateMessages(scope, thread, () => [{ role: 'user', content: 'hello' }]))
      .not.toThrow();

    expect(useAiChatStore.getState().messagesByThread[aiChatFullKey(scope, thread)]).toEqual([{ role: 'user', content: 'hello' }]);
    expect(useChatPersistenceStatus.getState().unavailable).toBe(true);
  });

  it('flags persistence as unavailable when reading or removing throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError'); });
    expect(chatSessionStorage.getItem('k')).toBeNull();
    expect(useChatPersistenceStatus.getState().unavailable).toBe(true);

    useChatPersistenceStatus.setState({ unavailable: false });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError'); });
    expect(() => chatSessionStorage.removeItem('k')).not.toThrow();
    expect(useChatPersistenceStatus.getState().unavailable).toBe(true);
  });

  it('clears the flag again after a write succeeds', () => {
    useChatPersistenceStatus.setState({ unavailable: true });

    chatSessionStorage.setItem('k', 'v');

    expect(sessionStorage.getItem('k')).toBe('v');
    expect(useChatPersistenceStatus.getState().unavailable).toBe(false);
  });
});
