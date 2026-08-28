import { useState, useCallback, useRef, useEffect } from 'react';

interface UseResizableOptions {
  initialSize: number;
  minSize: number;
  maxSize: number;
  direction: 'horizontal' | 'vertical';
  /** For right-side panels where dragging left increases size */
  reverse?: boolean;
}

export function useResizable({ initialSize, minSize, maxSize, direction, reverse = false }: UseResizableOptions) {
  const [size, setSize] = useState(initialSize);
  const isDragging = useRef(false);
  const startPos = useRef(0);
  const startSize = useRef(0);

  const onMouseDown = useCallback(
    // `startSizeOverride` lets a caller seed the drag from a freshly measured DOM size instead
    // of the tracked `size`, for panels that stay auto/content sized until the handle is grabbed.
    (e: React.MouseEvent, startSizeOverride?: number) => {
      e.preventDefault();
      isDragging.current = true;
      startPos.current = direction === 'horizontal' ? e.clientX : e.clientY;
      startSize.current = startSizeOverride ?? size;
      // Seeding from a measured size also syncs the state, so a consumer that switches from
      // auto sizing to `size`-driven sizing on mousedown renders at the measured height.
      if (startSizeOverride !== undefined) setSize(startSizeOverride);
      document.body.style.cursor = direction === 'horizontal' ? 'col-resize' : 'row-resize';
      document.body.style.userSelect = 'none';
    },
    [size, direction],
  );

  useEffect(() => {
    const onMouseMove = (e: MouseEvent) => {
      if (!isDragging.current) return;
      const pos = direction === 'horizontal' ? e.clientX : e.clientY;
      const delta = pos - startPos.current;
      const newSize = reverse
        ? startSize.current - delta
        : startSize.current + delta;
      setSize(Math.min(maxSize, Math.max(minSize, newSize)));
    };

    const onMouseUp = () => {
      if (!isDragging.current) return;
      isDragging.current = false;
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
    return () => {
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
    };
  }, [direction, minSize, maxSize, reverse]);

  const onDoubleClick = useCallback(() => {
    setSize(initialSize);
  }, [initialSize]);

  const handleProps = {
    onMouseDown,
    onDoubleClick,
  };

  return { size, handleProps };
}
