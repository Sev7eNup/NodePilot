import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import { ToolbarSection } from '../../../../components/designer/header/ToolbarSection';
import { ToolbarGlowContext } from '../../../../components/designer/header/toolbarGlowContext';
import { SECTION_GLOW_COLOR } from '../../../../components/designer/header/sectionColors';

describe('ToolbarSection', () => {
  it('renders an aria-hidden bloom and sets the per-section color variable', () => {
    const { container } = render(
      <ToolbarSection id="run">
        <button>X</button>
      </ToolbarSection>,
    );
    const section = container.querySelector('.np-toolbar-section') as HTMLElement;
    expect(section).toBeTruthy();
    expect(section.style.getPropertyValue('--np-section-color')).toBe(SECTION_GLOW_COLOR.run);

    const bloom = container.querySelector('.np-glow-bloom');
    expect(bloom).toBeTruthy();
    expect(bloom?.getAttribute('aria-hidden')).toBe('true');

    // The cluster's own controls still render alongside the decorative bloom.
    expect(container.querySelector('button')?.textContent).toBe('X');
  });

  it('keeps the tight-flex layout classes plus the segment-tray chrome', () => {
    const { container } = render(
      <ToolbarSection id="history" className="extra-class">
        <button>H</button>
      </ToolbarSection>,
    );
    const section = container.querySelector('.np-toolbar-section') as HTMLElement;
    expect(section.className).toContain('flex');
    expect(section.className).toContain('items-center');
    expect(section.className).toContain('gap-0.5');
    // Segment tray introduced by the toolbar redesign — buttons render as tiles on this tint.
    expect(section.className).toContain('bg-surface-container/75');
    expect(section.className).toContain('rounded-lg');
    expect(section.className).toContain('extra-class');
  });

  it('registers its element with the provider and unregisters on unmount', () => {
    const unregister = vi.fn();
    const register = vi.fn(() => unregister);
    const { container, unmount } = render(
      <ToolbarGlowContext.Provider value={{ register }}>
        <ToolbarSection id="lifecycle">
          <button>L</button>
        </ToolbarSection>
      </ToolbarGlowContext.Provider>,
    );
    const section = container.querySelector('.np-toolbar-section');
    expect(register).toHaveBeenCalledTimes(1);
    expect(register).toHaveBeenCalledWith(section);

    unmount();
    expect(unregister).toHaveBeenCalledTimes(1);
  });
});
