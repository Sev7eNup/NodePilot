import { create } from 'zustand';
import { registerAuthBoundaryLiveStateClearer } from '../security/authBoundary';

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
 * so a failure toast cannot slip by unnoticed while the user looks elsewhere.
 */
/**
 * The api client formats every structured server error as `message (CODE)`, so matching the code
 * here suppresses it for every mutation call site at once. Only the outage code is suppressed,
 * because the outage banner already says the same thing. `DATABASE_TIMEOUT` still toasts: the
 * breaker stays closed for it, so no banner is shown and a busy database would otherwise look
 * like an empty installation.
 */
function isDatabaseOutageMessage(message: string): boolean {
  return message.includes('DATABASE_UNAVAILABLE');
}

export const useToastStore = create<ToastStore>()((set) => ({
  toasts: [],
  push: (kind, message, timeoutMs) => {
    // A suppressed push returns -1, an id that never exists, so dismiss(-1) is a no-op. Only
    // error toasts are filtered; an outage must not swallow success messages.
    if (kind === 'error' && isDatabaseOutageMessage(message)) return -1;

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
 * Imperative helper that works outside React (hooks, lib/ modules, command palette)
 * via getState(), mirroring how App.tsx drives authStore/themeStore at bundle load.
 */
export const toast = {
  success: (message: string, timeoutMs?: number) => useToastStore.getState().push('success', message, timeoutMs),
  error: (message: string, timeoutMs?: number) => useToastStore.getState().push('error', message, timeoutMs),
  info: (message: string, timeoutMs?: number) => useToastStore.getState().push('info', message, timeoutMs),
};

// ToastHost is mounted outside ProtectedRoute. Messages can contain workflow/database names, so
// do not display User A's notifications after the browser switches to User B.
registerAuthBoundaryLiveStateClearer(() => useToastStore.setState({ toasts: [] }));
