import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { AtelierThemeToggle } from '../../components/designer/header/AtelierThemeToggle';
import { useDesignStore } from '../../stores/designStore';

/**
 * The AtelierThemeToggle switches the designer between the Atelier design language and the
 * classic look. It stays a `role="switch"` button rather than a checkbox input, so that e2e
 * specs using `getByRole('checkbox').first()` do not match it in the editor header, and it
 * both reflects and drives `designStore.designerTheme`.
 */
describe('AtelierThemeToggle', () => {
  beforeEach(() => {
    useDesignStore.setState({ designerTheme: 'atelier' });
  });

  it('rendersAsSwitch_neverAsCheckbox', () => {
    render(<AtelierThemeToggle />);
    expect(screen.getByRole('switch')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('reflectsAtelierState_asAriaChecked', () => {
    render(<AtelierThemeToggle />);
    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'true');
  });

  it('click_togglesDesignerThemeInStore_andAriaState', () => {
    render(<AtelierThemeToggle />);
    fireEvent.click(screen.getByRole('switch'));
    expect(useDesignStore.getState().designerTheme).toBe('classic');
    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'false');
    fireEvent.click(screen.getByRole('switch'));
    expect(useDesignStore.getState().designerTheme).toBe('atelier');
    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'true');
  });

  it('classicState_rendersUnchecked', () => {
    useDesignStore.setState({ designerTheme: 'classic' });
    render(<AtelierThemeToggle />);
    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'false');
  });
});
