import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ModalShell } from '../../../components/common/ModalShell';

describe('ModalShell', () => {
  it('marks the default panel with np-modal-panel', () => {
    // The class is the hook the dark-mode lift hangs on (index.css): a dialog is a raised
    // surface, so the recessed `.input-field` inside it has something to sink into. Without
    // it the fields paint themselves the panel's own colour and disappear.
    render(<ModalShell><p>body</p></ModalShell>);
    const panel = screen.getByText('body').parentElement!;
    expect(panel).toHaveClass('np-modal-panel');
    expect(panel).toHaveClass('bg-surface-lowest');
  });

  it('leaves a caller-supplied panelClassName untouched', () => {
    render(<ModalShell panelClassName="my-own-panel"><p>body</p></ModalShell>);
    const panel = screen.getByText('body').parentElement!;
    expect(panel).toHaveClass('my-own-panel');
    expect(panel).not.toHaveClass('np-modal-panel');
  });

  it('closes on backdrop click and keeps clicks inside the panel', () => {
    const onClose = vi.fn();
    render(<ModalShell onClose={onClose}><p>body</p></ModalShell>);

    fireEvent.click(screen.getByText('body'));
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(document.querySelector('.np-anim-backdrop')!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
