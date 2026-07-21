import {
  useEffect,
  useLayoutEffect,
  useState,
  type CSSProperties,
  type ReactNode,
  type RefObject,
} from 'react';
import { createPortal } from 'react-dom';

const WIDTH = 288;
const MAX_HEIGHT = 480;
const VIEWPORT_MARGIN = 8;
const ANCHOR_GAP = 4;

type Position = {
  left: number;
  width: number;
  maxHeight: number;
  top?: number;
  bottom?: number;
};

function computePosition(rect: DOMRect): Position {
  const viewportWidth = globalThis.innerWidth;
  const viewportHeight = globalThis.innerHeight;
  const width = Math.min(WIDTH, Math.max(0, viewportWidth - VIEWPORT_MARGIN * 2));
  const left = Math.max(
    VIEWPORT_MARGIN,
    Math.min(rect.left, viewportWidth - width - VIEWPORT_MARGIN),
  );
  const roomBelow = viewportHeight - rect.bottom - ANCHOR_GAP - VIEWPORT_MARGIN;
  const roomAbove = rect.top - ANCHOR_GAP - VIEWPORT_MARGIN;
  const openAbove = roomBelow < 200 && roomAbove > roomBelow;
  const availableHeight = Math.max(96, openAbove ? roomAbove : roomBelow);
  const vertical = openAbove
    ? { bottom: viewportHeight - rect.top + ANCHOR_GAP }
    : { top: rect.bottom + ANCHOR_GAP };

  return {
    left,
    width,
    maxHeight: Math.min(MAX_HEIGHT, availableHeight),
    ...vertical,
  };
}

/**
 * Viewport-anchored shell for property pickers. The portal is essential: property Sections
 * and the panel scroller intentionally clip their contents, so an in-tree absolute popover
 * can never overlap the following Sections reliably.
 */
export function AnchoredPickerPopover({
  open,
  anchorRef,
  popoverRef,
  children,
  surfaceClass = 'bg-surface-lowest border-outline-variant/40',
}: Readonly<{
  open: boolean;
  anchorRef: RefObject<HTMLElement | null>;
  popoverRef: RefObject<HTMLDivElement | null>;
  children: ReactNode;
  surfaceClass?: string;
}>) {
  const [position, setPosition] = useState<Position | null>(null);

  useLayoutEffect(() => {
    if (!open || !anchorRef.current) {
      setPosition(null);
      return;
    }
    setPosition(computePosition(anchorRef.current.getBoundingClientRect()));
  }, [open, anchorRef]);

  useEffect(() => {
    if (!open) return;
    const update = () => {
      if (anchorRef.current) setPosition(computePosition(anchorRef.current.getBoundingClientRect()));
    };
    globalThis.addEventListener('scroll', update, true);
    globalThis.addEventListener('resize', update);
    return () => {
      globalThis.removeEventListener('scroll', update, true);
      globalThis.removeEventListener('resize', update);
    };
  }, [open, anchorRef]);

  if (!open || !position) return null;

  return createPortal(
    <div
      ref={popoverRef}
      data-testid="anchored-picker-popover"
      className={`fixed z-[70] flex flex-col overflow-hidden rounded-md border shadow-2xl ${surfaceClass}`}
      style={position as CSSProperties}
    >
      {children}
    </div>,
    document.body,
  );
}
