import { useState, useCallback, useRef, useEffect } from 'react';

interface UseResizableOptions {
  initialSize: number;
  minSize: number;
  maxSize: number;
  direction: 'horizontal' | 'vertical';
  /** For right-side panels where dragging left increases size */
  reverse?: boolean;
  /** Pointer travel per unit of size change. A centred element moves both of its edges, so one
   *  edge only covers half the size change; `scale: 2` keeps the handle under the cursor. */
  scale?: number;
}

export function useResizable({ initialSize, minSize, maxSize, direction, reverse = false, scale = 1 }: UseResizableOptions) {
  const [size, setSizeState] = useState(initialSize);
  const isDragging = useRef(false);
  const startPos = useRef(0);
  const startSize = useRef(0);
  // Direction of the active drag, fixed on mousedown so a mirrored handle on the opposite edge
  // can drive the same size in the opposite direction.
  const dragSign = useRef(1);

  const beginDrag = useCallback(
    // `startSizeOverride` lets a caller seed the drag from a freshly measured DOM size instead
    // of the tracked `size`, for panels that stay auto/content sized until the handle is grabbed.
    (e: React.MouseEvent, reversed: boolean, startSizeOverride?: number) => {
      e.preventDefault();
      isDragging.current = true;
      dragSign.current = reversed ? -1 : 1;
      startPos.current = direction === 'horizontal' ? e.clientX : e.clientY;
      startSize.current = startSizeOverride ?? size;
      // Seeding from a measured size also syncs the state, so a consumer that switches from
      // auto sizing to `size`-driven sizing on mousedown renders at the measured height.
      if (startSizeOverride !== undefined) setSizeState(startSizeOverride);
      document.body.style.cursor = direction === 'horizontal' ? 'col-resize' : 'row-resize';
      document.body.style.userSelect = 'none';
    },
    [size, direction],
  );

  const onMouseDown = useCallback(
    (e: React.MouseEvent, startSizeOverride?: number) => beginDrag(e, reverse, startSizeOverride),
    [beginDrag, reverse],
  );

  const onMirrorMouseDown = useCallback(
    (e: React.MouseEvent, startSizeOverride?: number) => beginDrag(e, !reverse, startSizeOverride),
    [beginDrag, reverse],
  );

  useEffect(() => {
    const onMouseMove = (e: MouseEvent) => {
      if (!isDragging.current) return;
      const pos = direction === 'horizontal' ? e.clientX : e.clientY;
      const delta = pos - startPos.current;
      const newSize = startSize.current + delta * dragSign.current * scale;
      setSizeState(Math.min(maxSize, Math.max(minSize, newSize)));
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
  }, [direction, minSize, maxSize, scale]);

  const onDoubleClick = useCallback(() => {
    setSizeState(initialSize);
  }, [initialSize]);

  /** Clamped setter for consumers that set the size outside a drag, e.g. resetting to a
   *  default that differs from the restored `initialSize`. */
  const setSize = useCallback(
    (next: number) => setSizeState(Math.min(maxSize, Math.max(minSize, next))),
    [minSize, maxSize],
  );

  const handleProps = {
    onMouseDown,
    onDoubleClick,
  };

  /** Props for a second handle on the opposite edge; drives the same size, inverted. */
  const mirrorHandleProps = {
    onMouseDown: onMirrorMouseDown,
    onDoubleClick,
  };

  return { size, handleProps, mirrorHandleProps, setSize };
}
