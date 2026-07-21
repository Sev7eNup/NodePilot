import { describe, it, expect, vi, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { ToolbarGlow } from '../../../../components/designer/header/ToolbarGlow';

/** Records the event types passed to document.addEventListener during a render. */
function spyOnDocumentListeners(): string[] {
  const types: string[] = [];
  const orig = document.addEventListener;
  vi.spyOn(document, 'addEventListener').mockImplementation(function (
    this: Document,
    ...args: Parameters<Document['addEventListener']>
  ) {
    types.push(String(args[0]));
    return orig.apply(this ?? document, args);
  });
  return types;
}

describe('ToolbarGlow', () => {
  const realMatchMedia = window.matchMedia;
  afterEach(() => {
    window.matchMedia = realMatchMedia;
    vi.restoreAllMocks();
  });

  it('renders its children inside the styled root container', () => {
    const { container } = render(
      <ToolbarGlow className="flex items-center gap-6">
        <button>A</button>
      </ToolbarGlow>,
    );
    const root = container.firstElementChild as HTMLElement;
    expect(root.className).toContain('flex items-center gap-6');
    expect(root.querySelector('button')?.textContent).toBe('A');
  });

  it('tracks the cursor on document (pointermove) so approach-from-below is detected', () => {
    // Default matchMedia stub (setup.ts) reports matches:false → motion allowed.
    const types = spyOnDocumentListeners();
    render(
      <ToolbarGlow>
        <button>A</button>
      </ToolbarGlow>,
    );
    expect(types).toContain('pointermove');
  });

  it('attaches NO pointer listeners under prefers-reduced-motion', () => {
    window.matchMedia = ((q: string) => ({
      matches: true,
      media: q,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;

    const types = spyOnDocumentListeners();
    render(
      <ToolbarGlow>
        <button>A</button>
      </ToolbarGlow>,
    );
    expect(types).not.toContain('pointermove');
    expect(types).not.toContain('pointerleave');
  });
});
