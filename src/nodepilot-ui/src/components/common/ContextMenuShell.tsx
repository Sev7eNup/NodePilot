import { useEffect, useRef, type ReactNode } from 'react';

interface ContextMenuShellProps {
  x: number;
  y: number;
  onClose: () => void;
  /** "absolute" for in-canvas menus (EdgeContextMenu), "fixed" for viewport-anchored
   *  menus (SharedFolderContextMenu). */
  positioning?: 'absolute' | 'fixed';
  /** Tailwind z-index utility (e.g. "z-30", "z-50"). */
  zIndex?: string;
  /** data-testid forwarded to the outer div. */
  testId?: string;
  children: ReactNode;
}

/**
 * Outside-click + Escape close behaviour + positioned card chrome shared by all right-click
 * pop-ups (EdgeContextMenu, SharedFolderContextMenu, …). Children render the menu items
 * (use the sibling MenuItem helper for consistent styling).
 */
export function ContextMenuShell({
  x, y, onClose, positioning = 'absolute', zIndex = 'z-30', testId, children,
}: Readonly<ContextMenuShellProps>) {
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleMouseDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) onClose();
    };
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [onClose]);

  return (
    <div
      ref={menuRef}
      className={`${positioning} ${zIndex} bg-surface-lowest border border-outline-variant/30 rounded-lg shadow-2xl py-1 min-w-[180px]`}
      style={{ left: x, top: y }}
      data-testid={testId}
    >
      {children}
    </div>
  );
}

/** Caller utility: wraps an action so it runs and then closes the menu in one step. */
export function makeMenuAction(onClose: () => void) {
  return (fn: () => void) => () => { fn(); onClose(); };
}

interface MenuItemProps {
  icon: ReactNode;
  label: string;
  onClick: () => void;
  danger?: boolean;
  testId?: string;
}

/** Styled menu row used by every ContextMenuShell child. */
export function ContextMenuItem({ icon, label, onClick, danger, testId }: Readonly<MenuItemProps>) {
  return (
    <button
      type="button"
      className={`w-full flex items-center gap-2.5 px-3 py-1.5 text-left text-xs font-label transition-colors
        ${danger ? 'text-error hover:bg-error/10' : 'text-on-surface hover:bg-surface-high'}`}
      onClick={onClick}
      data-testid={testId}
    >
      <span className={danger ? 'text-error' : 'text-on-surface-variant'}>{icon}</span>
      {label}
    </button>
  );
}
