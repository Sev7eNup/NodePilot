import { createPortal } from 'react-dom';
import { ActivityPickerGrid } from './ActivityPickerGrid';

export function EdgeInserter({ onPick, onClose }: Readonly<{
  x: number;
  y: number;
  onPick: (type: string, label: string) => void;
  onClose: () => void;
}>) {
  // Portaled to document.body so the picker escapes every ancestor stacking
  // context — in particular the ExecutionPanel's `isolate`, which otherwise
  // overlaps the fixed z-40 picker rendered inside <main>. Always renders
  // topmost, regardless of where on the canvas the drag originated.
  return createPortal(
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-center justify-center bg-black/10"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="w-[360px] max-w-[90vw] max-h-[70vh] overflow-y-auto bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <ActivityPickerGrid onPick={onPick} onClose={onClose} />
      </div>
    </div>,
    document.body,
  );
}
