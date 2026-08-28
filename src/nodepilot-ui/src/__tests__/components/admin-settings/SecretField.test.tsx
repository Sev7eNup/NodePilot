import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import {
  SecretField,
  serializeSecretField,
  UNCHANGED_SECRET_SENTINEL,
  type SecretFieldMode,
} from '../../../components/admin-settings/SecretField';

// i18n is initialised globally in tests/setup, so `t('adminSettings:…')` resolves to the
// real German translations. That is good enough for these tests.

function renderField(initial: { mode: SecretFieldMode; hasPersistedValue: boolean; value?: string }) {
  const onModeChange = vi.fn();
  const onValueChange = vi.fn();
  const utils = render(
    <SecretField
      label="Password"
      inputId="test-password"
      hasPersistedValue={initial.hasPersistedValue}
      mode={initial.mode}
      value={initial.value ?? ''}
      onModeChange={onModeChange}
      onValueChange={onValueChange}
    />,
  );
  return { ...utils, onModeChange, onValueChange };
}

describe('SecretField', () => {
  it('keep mode with persisted value shows masked input + change/clear affordances', () => {
    renderField({ mode: 'keep', hasPersistedValue: true });
    // The mask "********" is rendered as the input's value, so query it by display value.
    const masked = screen.getByDisplayValue('********');
    expect(masked).toBeDisabled();
    expect(screen.getByRole('button', { name: /neu setzen|set new/i })).toBeInTheDocument();
  });

  it('keep mode without persisted value falls through to plain input (change mode)', () => {
    renderField({ mode: 'keep', hasPersistedValue: false });
    // With no stored secret to keep there is no "********" placeholder; the user types directly.
    expect(screen.queryByDisplayValue('********')).not.toBeInTheDocument();
  });

  it('clicking change requests mode transition to change', () => {
    const { onModeChange } = renderField({ mode: 'keep', hasPersistedValue: true });
    fireEvent.click(screen.getByRole('button', { name: /neu setzen|set new/i }));
    expect(onModeChange).toHaveBeenCalledWith('change');
  });

  it('typing in change mode bubbles plaintext + auto-promotes mode if currently keep', () => {
    const { onModeChange, onValueChange } = renderField({ mode: 'change', hasPersistedValue: true });
    const input = screen.getByLabelText('Password') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'super-secret' } });
    expect(onValueChange).toHaveBeenCalledWith('super-secret');
    // onModeChange does not fire when the mode is already 'change'.
    expect(onModeChange).not.toHaveBeenCalled();
  });

  it('clear mode disables the input and shows a Cancel button', () => {
    const { onModeChange } = renderField({ mode: 'clear', hasPersistedValue: true });
    const inputs = screen.getAllByRole('textbox');
    expect(inputs.some((i) => (i as HTMLInputElement).disabled)).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: /abbrechen|cancel/i }));
    expect(onModeChange).toHaveBeenCalledWith('keep');
  });
});

describe('serializeSecretField', () => {
  it('keep → sentinel (server preserves persisted ciphertext)', () => {
    expect(serializeSecretField('keep', 'ignored')).toBe(UNCHANGED_SECRET_SENTINEL);
  });

  it('change → typed plaintext (server encrypts on persist)', () => {
    expect(serializeSecretField('change', 'hunter2')).toBe('hunter2');
  });

  it('clear → null (server drops the field)', () => {
    expect(serializeSecretField('clear', 'ignored')).toBeNull();
  });
});
