import { ActivityPickerGrid } from './ActivityPickerGrid';

export function EdgeInserter({ onPick, onClose }: Readonly<{
  x: number;
  y: number;
  onPick: (type: string, label: string) => void;
  onClose: () => void;
}>) {
  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-40 flex items-center justify-center bg-black/10"
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
    </div>
  );
}
