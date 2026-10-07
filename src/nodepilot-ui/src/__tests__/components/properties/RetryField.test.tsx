import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { RetryField, DEFAULT_RETRY } from '../../../components/designer/properties/shared';

describe('RetryField', () => {
  it('RetryField_NoRetryConfig_ShowsSwitchOffAndNoFields', () => {
    render(<RetryField value={undefined} onChange={vi.fn()} />);
    expect(screen.getByRole('checkbox')).not.toBeChecked();
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  it('RetryField_SwitchOn_EmitsEngineDefaults', () => {
    const onChange = vi.fn();
    render(<RetryField value={undefined} onChange={onChange} />);
    fireEvent.click(screen.getByRole('checkbox'));
    expect(onChange).toHaveBeenCalledWith(DEFAULT_RETRY);
  });

  it('RetryField_SwitchOff_RemovesRetry', () => {
    const onChange = vi.fn();
    render(<RetryField value={{ maxAttempts: 4, backoff: 'linear' }} onChange={onChange} />);
    fireEvent.click(screen.getByRole('checkbox'));
    expect(onChange).toHaveBeenCalledWith(undefined);
  });

  it('RetryField_ExistingConfig_FillsFieldsWithDefaultsForMissingKeys', () => {
    render(<RetryField value={{ maxAttempts: 3, backoff: 'exponential' }} onChange={vi.fn()} />);
    expect(screen.getByRole('checkbox')).toBeChecked();
    expect((screen.getByRole('combobox') as HTMLSelectElement).value).toBe('exponential');
    const numbers = screen.getAllByRole('spinbutton') as HTMLInputElement[];
    expect(numbers.map((n) => n.value)).toEqual(['3', '1000', '30000']);
  });

  it('RetryField_MaxAttemptsAboveEngineCap_ClampsTo20', () => {
    const onChange = vi.fn();
    render(<RetryField value={{ maxAttempts: 3 }} onChange={onChange} />);
    fireEvent.change(screen.getAllByRole('spinbutton')[0], { target: { value: '99' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ maxAttempts: 20 }));
  });

  it('RetryField_ChangeBackoff_KeepsOtherValues', () => {
    const onChange = vi.fn();
    render(<RetryField value={{ maxAttempts: 5, backoff: 'fixed', initialDelayMs: 200, maxDelayMs: 5000 }} onChange={onChange} />);
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'linear' } });
    expect(onChange).toHaveBeenCalledWith({ maxAttempts: 5, backoff: 'linear', initialDelayMs: 200, maxDelayMs: 5000 });
  });
});
