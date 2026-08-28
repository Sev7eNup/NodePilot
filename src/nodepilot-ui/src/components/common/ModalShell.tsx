import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';

/**
 * Shared overlay for "add / create / edit" dialogs.
 * Every dialog opens top-aligned at the same offset, and the backdrop scrolls so a
 * form taller than the viewport stays fully reachable. Rendered through a portal to
 * `document.body` (wrapped in `np-shell`) so no transformed ancestor can clip it and
 * theme tokens still apply. Click-outside and Escape both close when `onClose` is set.
 */
export function ModalShell({
  onClose,
  children,
  maxWidth = 'max-w-md',
  z = 'z-50',
  panelClassName,
}: Readonly<{
  onClose?: () => void;
  children: ReactNode;
  maxWidth?: string;
  z?: string;
  panelClassName?: string;
}>) {
  return createPortal(
    <div className="np-shell">
      <div
        className={`np-anim-backdrop fixed inset-0 ${z} overflow-y-auto bg-black/30 backdrop-blur-sm`}
        onClick={onClose}
        onKeyDown={(e) => { if (e.key === 'Escape') onClose?.(); }}
        role="presentation"
        tabIndex={-1}
      >
        {/* min-h-full + items-start = uniform top anchor; pt-[10vh] is the shared offset.
            Tighter horizontal/top spacing on phones so the dialog isn't cramped. */}
        <div className="flex min-h-full justify-center items-start px-3 sm:px-4 pb-8 sm:pb-12 pt-[6vh] sm:pt-[10vh]">
          <div
            className={panelClassName ?? `np-modal-panel bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 p-4 sm:p-6 w-full ${maxWidth} shrink-0`}
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => e.stopPropagation()}
            role="presentation"
          >
            {children}
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
