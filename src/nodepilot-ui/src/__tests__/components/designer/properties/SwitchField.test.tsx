import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SwitchField } from '../../../../components/designer/properties/shared';

describe('SwitchField', () => {
  it('renders a checkbox with switch styling and fires onChange', async () => {
    const onChange = vi.fn();
    render(
      <SwitchField label="Auto-Logging" ariaLabel="Auto-Logging" checked={false} onChange={onChange} stateText="Aus" />,
    );
    const box = screen.getByRole('checkbox', { name: 'Auto-Logging' });
    expect(box).toHaveClass('np-switch');
    expect(box).not.toBeChecked();
    expect(screen.getByText('Aus')).toBeInTheDocument();
    await userEvent.click(box);
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('disabled switch does not fire onChange', async () => {
    const onChange = vi.fn();
    render(
      <SwitchField ariaLabel="Prozess-Isolation" checked={false} disabled onChange={onChange} stateText="Aus" />,
    );
    const box = screen.getByRole('checkbox', { name: 'Prozess-Isolation' });
    expect(box).toBeDisabled();
    await userEvent.click(box).catch(() => {});
    expect(onChange).not.toHaveBeenCalled();
  });
});
