import { create } from 'zustand';
import type { StateStorage } from 'zustand/middleware';

/** Persistence is optional: storage denial/quota must never interrupt an in-memory send. */
export const useChatPersistenceStatus = create<{ unavailable: boolean; trimmed: boolean }>(() => ({
  unavailable: false, trimmed: false,
}));

export const chatSessionStorage: StateStorage = {
  getItem: (name) => {
    try { return globalThis.sessionStorage.getItem(name); }
    catch { useChatPersistenceStatus.setState({ unavailable: true }); return null; }
  },
  setItem: (name, value) => {
    try {
      globalThis.sessionStorage.setItem(name, value);
      useChatPersistenceStatus.setState({ unavailable: false });
    } catch { useChatPersistenceStatus.setState({ unavailable: true }); }
  },
  removeItem: (name) => {
    try { globalThis.sessionStorage.removeItem(name); }
    catch { useChatPersistenceStatus.setState({ unavailable: true }); }
  },
};
