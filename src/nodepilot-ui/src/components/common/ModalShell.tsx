import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';

/**
 * Shared overlay for "add / create / edit" dialogs. Three goals:
 *  1. Uniform position — every dialog opens top-aligned at the SAME offset, so
 *     clicking "Add X" anywhere in the app feels consistent.
 *  2. Never clip — the backdrop is the scroll container and the panel lives in a
 *     `min-h-full` flex wrapper with `shrink-0`, so a form taller than the
 *     viewport scrolls in full (top AND bottom reachable) instead of getting cut
 *     off, while a short form still sits at the same top spot.
 *  3. Always viewport-anchored — rendered through a portal to `document.body` so
 *     no transformed/animated ancestor (e.g. a page's `np-fade-up` entrance) can
 *     become the containing block and trap `fixed inset-0` inside the page's box
 *     (which clipped tall dialogs on short pages). The portal is wrapped in
 *     `np-shell` so the app-shell theme tokens (dark-orange accent, surfaces)
 *     still apply outside the main shell subtree.
 * Click-outside and Escape both close (when `onClose` is provided).
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
            className={panelClassName ?? `bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 p-4 sm:p-6 w-full ${maxWidth} shrink-0`}
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
