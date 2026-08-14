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
 * so a failure toast can't slip by unnoticed while the user looks elsewhere.
 */
/**
 * The api client formats every structured server error as `message (CODE)`, so the code is reliably
 * present in the display string — the only thing the ~50 mutation `onError` call sites hand to
 * `toast.error(...)`. Matching here, at the sink, suppresses them all without touching a call site.
 *
 * ONLY the outage code is suppressed, and only because the banner is on screen saying the same
 * thing. `DATABASE_TIMEOUT` deliberately still toasts: the breaker is closed for it, so no banner
 * exists, and swallowing it silently would reproduce the original defect — a busy database looking
 * exactly like an empty installation, with nothing on screen to act on.
 */
function isDatabaseOutageMessage(message: string): boolean {
  return message.includes('DATABASE_UNAVAILABLE');
}

export const useToastStore = create<ToastStore>()((set) => ({
  toasts: [],
  push: (kind, message, timeoutMs) => {
    // Contract note: a suppressed push returns -1, an id that never exists — dismiss(-1) is a
    // harmless no-op. Only error toasts are filtered; an outage must not eat success messages.
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
 * Imperative helper — works outside React (hooks, lib/ modules, command palette)
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
