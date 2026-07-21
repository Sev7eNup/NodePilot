import { describe, it, expect, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useResizable } from '../../hooks/useResizable';

describe('useResizable', () => {
  afterEach(() => {
    // Clean up cursor style any test left behind — useResizable writes to document.body
    // during a drag, and a leak would propagate "col-resize" globally to later tests.
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
  });

  it('returnsInitialSize_beforeAnyInteraction', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 280, minSize: 100, maxSize: 600, direction: 'horizontal' }),
    );
    expect(result.current.size).toBe(280);
  });

  it('horizontalDrag_growsSizeByDeltaX', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 200, minSize: 100, maxSize: 500, direction: 'horizontal' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 100, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      window.dispatchEvent(new MouseEvent('mousemove', { clientX: 150, clientY: 0 }));
      // useResizable subscribes via document.addEventListener; jsdom routes window mousemove to document.
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 150, clientY: 0 }));
    });

    // delta = 150 - 100 = 50 → new size = 200 + 50 = 250
    expect(result.current.size).toBe(250);
  });

  it('reverseDrag_shrinksWhenPointerMovesRight', () => {
    // Right-side panel (PropertiesPanel): dragging the splitter LEFT must increase the
    // panel width because the splitter sits on the panel's left edge. The reverse flag
    // inverts the delta sign — pin the contract.
    const { result } = renderHook(() =>
      useResizable({ initialSize: 300, minSize: 100, maxSize: 600, direction: 'horizontal', reverse: true }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 500, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 600, clientY: 0 }));
    });

    // delta = 600 - 500 = 100 → reversed → new size = 300 - 100 = 200
    expect(result.current.size).toBe(200);
  });

  it('clampsToMaxSize', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 200, minSize: 100, maxSize: 300, direction: 'horizontal' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 0, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 999, clientY: 0 }));
    });

    expect(result.current.size).toBe(300);
  });

  it('clampsToMinSize', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 200, minSize: 80, maxSize: 500, direction: 'horizontal' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 0, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: -999, clientY: 0 }));
    });

    expect(result.current.size).toBe(80);
  });

  it('verticalDrag_usesClientY', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 100, minSize: 50, maxSize: 400, direction: 'vertical' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 0, clientY: 50 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 0, clientY: 110 }));
    });

    expect(result.current.size).toBe(160);
  });

  it('startSizeOverride_seedsDragFromMeasuredSizeNotState', () => {
    // A panel that defaults to auto/content height seeds the drag from its measured DOM
    // height (not the hook's tracked initialSize), so the first pull continues from what
    // the user sees instead of jumping. delta is applied on top of the override.
    const { result } = renderHook(() =>
      useResizable({ initialSize: 360, minSize: 100, maxSize: 800, direction: 'vertical' }),
    );

    act(() => {
      // initialSize is 360, but the box currently renders at 220px (auto/content) — seed 220.
      result.current.handleProps.onMouseDown(
        { preventDefault: () => {}, clientX: 0, clientY: 100 } as React.MouseEvent,
        220,
      );
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 0, clientY: 150 }));
    });

    // delta = 150 - 100 = 50 → size = 220 (override) + 50 = 270, NOT 360 + 50.
    expect(result.current.size).toBe(270);
  });

  it('startSizeOverride_updatesSizeStateOnMouseDown_beforeAnyMove', () => {
    // Regression: a click on the corner handle (mousedown → mouseup, no drag) must NOT
    // jump the panel to initialSize. When seeded from the measured height, the state is
    // synced on mousedown so a consumer switching to `size`-driven height renders at the
    // measured height immediately instead of flashing to initialSize.
    const { result } = renderHook(() =>
      useResizable({ initialSize: 360, minSize: 100, maxSize: 800, direction: 'vertical' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown(
        { preventDefault: () => {}, clientX: 0, clientY: 100 } as React.MouseEvent,
        120, // measured content height
      );
    });

    // No mousemove yet — size must already reflect the override, not the 360 initialSize.
    expect(result.current.size).toBe(120);

    act(() => {
      document.dispatchEvent(new MouseEvent('mouseup'));
    });
    // A pure click leaves the panel at the seeded height.
    expect(result.current.size).toBe(120);
  });

  it('mouseUp_endsDrag_subsequentMoveDoesNotResize', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 200, minSize: 100, maxSize: 500, direction: 'horizontal' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 100, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 150, clientY: 0 }));
    });
    expect(result.current.size).toBe(250);

    act(() => {
      document.dispatchEvent(new MouseEvent('mouseup'));
    });
    // After mouseUp, further mousemove must not change size.
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 999, clientY: 0 }));
    });
    expect(result.current.size).toBe(250);
  });

  it('doubleClick_resetsToInitialSize', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 280, minSize: 100, maxSize: 600, direction: 'horizontal' }),
    );

    // First drag away from the initial size, then verify double-click snaps back.
    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 100, clientY: 0 } as React.MouseEvent);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: 200, clientY: 0 }));
      document.dispatchEvent(new MouseEvent('mouseup'));
    });
    expect(result.current.size).toBe(380);

    act(() => result.current.handleProps.onDoubleClick());
    expect(result.current.size).toBe(280);
  });

  it('mouseDown_setsCursorAndUserSelect', () => {
    const { result } = renderHook(() =>
      useResizable({ initialSize: 200, minSize: 100, maxSize: 500, direction: 'horizontal' }),
    );

    act(() => {
      result.current.handleProps.onMouseDown({ preventDefault: () => {}, clientX: 0, clientY: 0 } as React.MouseEvent);
    });

    // While dragging, the body cursor must show col-resize and text selection is suppressed
    // so the user doesn't accidentally select labels under the moving pointer.
    expect(document.body.style.cursor).toBe('col-resize');
    expect(document.body.style.userSelect).toBe('none');

    act(() => {
      document.dispatchEvent(new MouseEvent('mouseup'));
    });
    expect(document.body.style.cursor).toBe('');
    expect(document.body.style.userSelect).toBe('');
  });
});
