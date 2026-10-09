import { useEffect, useRef, useState, type MouseEvent } from 'react';

/** Owns one table's column widths and the lifetime of its active drag listeners. */
export function useColumnResize<K extends string>(
  initialWidths: Record<K, number>,
  minimumWidths: Partial<Record<K, number>> = {},
  defaultMinimum = 50,
) {
  const [colWidths, setColWidths] = useState(initialWidths);
  const stopRef = useRef<(() => void) | null>(null);
  useEffect(() => () => stopRef.current?.(), []);

  const startResize = (column: K, event: MouseEvent) => {
    event.preventDefault();
    event.stopPropagation();
    stopRef.current?.();
    const startX = event.clientX;
    const startWidth = colWidths[column];
    const minimum = minimumWidths[column] ?? defaultMinimum;
    const onMove = (move: globalThis.MouseEvent) => {
      const width = Math.max(minimum, startWidth + move.clientX - startX);
      setColWidths(current => ({ ...current, [column]: width }));
    };
    const stop = () => {
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', stop);
      stopRef.current = null;
    };
    stopRef.current = stop;
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', stop);
  };

  return { colWidths, startResize };
}
