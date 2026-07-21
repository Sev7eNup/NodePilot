import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { CopyButton } from '../../../components/common/CopyButton';

describe('CopyButton', () => {
  it('writes the text to the clipboard and swaps to a copied state', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    render(<CopyButton text="hello world" />);

    fireEvent.click(screen.getByRole('button', { name: /^copy$/i }));

    expect(writeText).toHaveBeenCalledWith('hello world');
    await waitFor(() => expect(screen.getByRole('button', { name: /copied/i })).toBeInTheDocument());
  });

  it('does not throw when the clipboard API is unavailable', () => {
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    render(<CopyButton text="x" />);
    expect(() => fireEvent.click(screen.getByRole('button', { name: /^copy$/i }))).not.toThrow();
  });
});
