import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from 'react';

const MIN_COLUMN_WIDTH = 80;
const MAX_COLUMN_WIDTH = 1_000;
const KEYBOARD_STEP = 16;

export interface ResizableColumn {
  key: string;
  defaultWidth: number;
}

export function useResizableColumns(columns: ResizableColumn[]) {
  const [widths, setWidths] = useState<Record<string, number>>({});
  const removeWindowListeners = useRef<(() => void) | null>(null);

  const getWidth = useCallback((column: ResizableColumn) => (
    widths[column.key] ?? column.defaultWidth
  ), [widths]);

  const resizeBy = useCallback((column: ResizableColumn, delta: number) => {
    setWidths((current) => ({
      ...current,
      [column.key]: clamp((current[column.key] ?? column.defaultWidth) + delta),
    }));
  }, []);

  const startResize = useCallback((
    column: ResizableColumn,
    event: ReactPointerEvent<HTMLElement>,
  ) => {
    event.preventDefault();
    event.stopPropagation();
    removeWindowListeners.current?.();

    const startX = event.clientX;
    const startWidth = widths[column.key] ?? column.defaultWidth;
    const previousCursor = document.body.style.cursor;
    const previousUserSelect = document.body.style.userSelect;
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';

    const handleMove = (moveEvent: PointerEvent) => {
      setWidths((current) => ({
        ...current,
        [column.key]: clamp(startWidth + moveEvent.clientX - startX),
      }));
    };

    const cleanup = () => {
      window.removeEventListener('pointermove', handleMove);
      window.removeEventListener('pointerup', cleanup);
      window.removeEventListener('pointercancel', cleanup);
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousUserSelect;
      removeWindowListeners.current = null;
    };

    removeWindowListeners.current = cleanup;
    window.addEventListener('pointermove', handleMove);
    window.addEventListener('pointerup', cleanup);
    window.addEventListener('pointercancel', cleanup);
  }, [widths]);

  useEffect(() => () => removeWindowListeners.current?.(), []);

  const totalWidth = useMemo(
    () => columns.reduce((sum, column) => sum + getWidth(column), 0),
    [columns, getWidth],
  );

  return { getWidth, resizeBy, startResize, totalWidth };
}

export function ResizeHandle({
  label,
  column,
  onPointerDown,
  onResizeBy,
}: Readonly<{
  label: string;
  column: ResizableColumn;
  onPointerDown: (column: ResizableColumn, event: ReactPointerEvent<HTMLElement>) => void;
  onResizeBy: (column: ResizableColumn, delta: number) => void;
}>) {
  return (
    <span
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
      tabIndex={0}
      onClick={(event) => event.stopPropagation()}
      onPointerDown={(event) => onPointerDown(column, event)}
      onKeyDown={(event) => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        event.preventDefault();
        event.stopPropagation();
        onResizeBy(column, event.key === 'ArrowRight' ? KEYBOARD_STEP : -KEYBOARD_STEP);
      }}
      className="absolute inset-y-0 -right-1 z-20 w-2 cursor-col-resize touch-none select-none outline-none after:absolute after:inset-y-1 after:left-1/2 after:w-px after:-translate-x-1/2 after:bg-outline-variant/40 hover:after:bg-primary focus-visible:after:bg-primary focus-visible:after:w-0.5"
    />
  );
}

function clamp(width: number): number {
  return Math.min(MAX_COLUMN_WIDTH, Math.max(MIN_COLUMN_WIDTH, width));
}
