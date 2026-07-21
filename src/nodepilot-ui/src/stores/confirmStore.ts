import { create } from 'zustand';

export interface ConfirmRequest {
  message: string;
  title?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Styles the confirm button as destructive (red) — use for deletes. */
  danger?: boolean;
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
    // Single-flight: a second confirm while one is open cancels the stale one,
    // matching how a second native confirm() would have replaced the first.
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
