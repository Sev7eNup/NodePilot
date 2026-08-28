import { create } from 'zustand';
import { registerAuthBoundaryLiveStateClearer } from '../security/authBoundary';

export interface ConfirmRequest {
  message: string;
  title?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Styles the confirm button as destructive (red). Use it for deletes. */
  danger?: boolean;
  /**
   * Rendered as a list between the message and the buttons. Naming the affected items lets the
   * reader check them against what they meant to select.
   */
  details?: readonly string[];
}

interface PendingConfirm extends ConfirmRequest {
  resolve: (ok: boolean) => void;
}

interface ConfirmStore {
  pending: PendingConfirm | null;
  open: (req: PendingConfirm) => void;
  settle: (ok: boolean) => void;
}

export const useConfirmStore = create<ConfirmStore>()((set, get) => ({
  pending: null,
  open: (req) => {
    // Single-flight: a second confirm while one is open cancels the stale one, matching how a
    // second native confirm() replaces the first.
    get().pending?.resolve(false);
    set({ pending: req });
  },
  settle: (ok) => {
    const p = get().pending;
    set({ pending: null });
    p?.resolve(ok);
  },
}));

/**
 * Promise-based replacement for the blocking native confirm(). Renders through
 * the globally mounted <ConfirmHost/>; callable from anywhere (React or not):
 *
 *   if (await confirmDialog(t('workflows:deleteConfirm'))) { ... }
 */
export function confirmDialog(req: ConfirmRequest | string): Promise<boolean> {
  const normalized: ConfirmRequest = typeof req === 'string' ? { message: req } : req;
  return new Promise<boolean>((resolve) => {
    useConfirmStore.getState().open({ ...normalized, resolve });
  });
}

// ConfirmHost lives outside ProtectedRoute. Cancel its continuation synchronously so a destructive
// User-A action can never be confirmed and resumed under User B's cookie after an auth switch.
registerAuthBoundaryLiveStateClearer(() => useConfirmStore.getState().settle(false));
