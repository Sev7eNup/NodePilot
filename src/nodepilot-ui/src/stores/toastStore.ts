import { create } from 'zustand';

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

interface ToastStore {
  toasts: Toast[];
  push: (kind: ToastKind, message: string, timeoutMs?: number) => number;
  dismiss: (id: number) => void;
}

let nextId = 0;

/**
 * Ephemeral (non-persisted) toast queue. Errors linger longer than success/info
 * so a failure toast can't slip by unnoticed while the user looks elsewhere.
 */
export const useToastStore = create<ToastStore>()((set) => ({
  toasts: [],
  push: (kind, message, timeoutMs) => {
    const id = ++nextId;
    set((s) => ({ toasts: [...s.toasts, { id, kind, message }] }));
    const ttl = timeoutMs ?? (kind === 'error' ? 8000 : 4000);
    setTimeout(() => {
      set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) }));
    }, ttl);
    return id;
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}));

/**
 * Imperative helper — works outside React (hooks, lib/ modules, command palette)
 * via getState(), mirroring how App.tsx drives authStore/themeStore at bundle load.
 */
export const toast = {
  success: (message: string, timeoutMs?: number) => useToastStore.getState().push('success', message, timeoutMs),
  error: (message: string, timeoutMs?: number) => useToastStore.getState().push('error', message, timeoutMs),
  info: (message: string, timeoutMs?: number) => useToastStore.getState().push('info', message, timeoutMs),
};
