import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { MouseEvent as ReactMouseEvent } from 'react';
import { useColumnResize } from '../../hooks/useColumnResize';

function startEvent(clientX: number) {
  return { clientX, preventDefault: vi.fn(), stopPropagation: vi.fn() } as unknown as ReactMouseEvent;
}

describe('useColumnResize', () => {
  it('keeps column-specific and default minimum widths and stops on mouseup', () => {
    const { result } = renderHook(() => useColumnResize({ name: 200, status: 180 }, { status: 160 }, 60));
    act(() => result.current.startResize('status', startEvent(100)));
    act(() => globalThis.dispatchEvent(new MouseEvent('mousemove', { clientX: 0 })));
    expect(result.current.colWidths).toEqual({ name: 200, status: 160 });
    act(() => globalThis.dispatchEvent(new MouseEvent('mouseup')));
    act(() => globalThis.dispatchEvent(new MouseEvent('mousemove', { clientX: 500 })));
    expect(result.current.colWidths.status).toBe(160);
    act(() => result.current.startResize('name', startEvent(300)));
    act(() => globalThis.dispatchEvent(new MouseEvent('mousemove', { clientX: 0 })));
    expect(result.current.colWidths.name).toBe(60);
  });

  it('replaces an active drag and removes its listeners when the table unmounts', () => {
    const remove = vi.spyOn(globalThis, 'removeEventListener');
    const { result, unmount } = renderHook(() => useColumnResize({ first: 100, second: 200 }));
    act(() => result.current.startResize('first', startEvent(0)));
    act(() => result.current.startResize('second', startEvent(0)));
    act(() => globalThis.dispatchEvent(new MouseEvent('mousemove', { clientX: 25 })));
    expect(result.current.colWidths).toEqual({ first: 100, second: 225 });
    remove.mockClear();
    unmount();
    expect(remove.mock.calls.map(([event]) => event)).toEqual(['mousemove', 'mouseup']);
    remove.mockRestore();
  });
});
