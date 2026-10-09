import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ModalShell } from '../../../components/common/ModalShell';
import { TypedPhraseConfirmDialog } from '../../../components/common/TypedPhraseConfirmDialog';
import { EditCellDialog } from '../../../components/dbviewer/EditCellDialog';

describe('ModalShell', () => {
  it('cancels typed confirmation from its focused input', () => {
    const cancel = vi.fn();
    const confirm = vi.fn();
    render(<TypedPhraseConfirmDialog phrase="WRITE" input="" onInput={vi.fn()} onCancel={cancel}
      onConfirm={confirm} title="Confirm" body="body" prompt="phrase" confirmLabel="Go" />);
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' });
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(confirm).not.toHaveBeenCalled();
  });

  it.each([false, true])('handles Escape in cell editor while saving=%s', (isSaving) => {
    const close = vi.fn();
    const save = vi.fn();
    render(<EditCellDialog tableName="Machines" column={{ name: 'Name', clrType: 'string', isNullable: false,
      maxLength: 200, isPrimaryKey: false, isMasked: false, isReadOnly: false }} currentValue="host"
      onSave={save} onClose={close} isSaving={isSaving} />);
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' });
    expect(close).toHaveBeenCalledTimes(isSaving ? 0 : 1);
    expect(save).not.toHaveBeenCalled();
  });

  it('closes from a focused input without closing a parent modal', () => {
    const outer = vi.fn();
    const inner = vi.fn();
    render(<ModalShell onClose={outer}><ModalShell onClose={inner}><input aria-label="inside" /></ModalShell></ModalShell>);
    const input = screen.getByLabelText('inside');
    input.focus();
    fireEvent.keyDown(input, { key: 'Escape' });
    expect(inner).toHaveBeenCalledTimes(1);
    expect(outer).not.toHaveBeenCalled();
  });

  it.each(['prevent', 'stop'])('respects Escape consumed by an editor (%s)', (consume) => {
    const close = vi.fn();
    render(<ModalShell onClose={close}><input aria-label="editor" onKeyDown={e => {
      if (e.key === 'Escape') { if (consume === 'prevent') e.preventDefault(); else e.stopPropagation(); }
    }} /></ModalShell>);
    fireEvent.keyDown(screen.getByLabelText('editor'), { key: 'Escape' });
    expect(close).not.toHaveBeenCalled();
  });

  it('marks the default panel with np-modal-panel', () => {
    // The class is the hook for the dark-mode lift in index.css: it makes the dialog a raised
    // surface so the recessed `.input-field` inside it has something to sink into. Without the
    // class the fields take the panel's own colour and become invisible.
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
