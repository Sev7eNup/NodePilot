import { describe, expect, it, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { ConfirmHost } from '../../../components/common/ConfirmHost';
import { confirmDialog, useConfirmStore } from '../../../stores/confirmStore';

describe('ConfirmHost', () => {
  beforeEach(() => {
    useConfirmStore.setState({ pending: null });
  });

  it('rendersNothing_whenNoPendingConfirm', () => {
    const { container } = render(<ConfirmHost />);
    expect(container).toBeEmptyDOMElement();
  });

  it('okClick_resolvesTrue', async () => {
    render(<ConfirmHost />);
    const promise = confirmDialog('Delete everything?');
    expect(await screen.findByText('Delete everything?')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'OK' }));
    await expect(promise).resolves.toBe(true);
    expect(screen.queryByText('Delete everything?')).not.toBeInTheDocument();
  });

  it('cancelClick_resolvesFalse', async () => {
    render(<ConfirmHost />);
    const promise = confirmDialog('Sure?');
    expect(await screen.findByText('Sure?')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await expect(promise).resolves.toBe(false);
  });

  it('customLabelsAndDanger_renderAsGiven', async () => {
    render(<ConfirmHost />);
    void confirmDialog({
      message: 'Drop it?',
      title: 'Careful',
      confirmLabel: 'Drop',
      cancelLabel: 'Keep',
      danger: true,
    });
    expect(await screen.findByText('Careful')).toBeInTheDocument();
    const dropBtn = screen.getByRole('button', { name: 'Drop' });
    expect(dropBtn.className).toContain('bg-error');
    expect(screen.getByRole('button', { name: 'Keep' })).toBeInTheDocument();
  });

  it('details_renderAsAList', async () => {
    // Listing the affected items by name lets the user check the dialog against what they
    // selected; a bare count cannot.
    render(<ConfirmHost />);
    void confirmDialog({
      message: 'Delete these?',
      details: ['/Finance — 3 workflows', '/Archive — 9 workflows'],
    });
    const list = await screen.findByTestId('confirm-details');
    expect(list.tagName).toBe('UL');
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(list).toHaveTextContent('/Finance — 3 workflows');
  });

  it('withoutDetails_rendersNoList', async () => {
    // Callers that pass no details must not get an empty list box.
    render(<ConfirmHost />);
    void confirmDialog('Just a question?');
    expect(await screen.findByText('Just a question?')).toBeInTheDocument();
    expect(screen.queryByTestId('confirm-details')).not.toBeInTheDocument();
  });

  it('emptyDetails_rendersNoList', async () => {
    render(<ConfirmHost />);
    void confirmDialog({ message: 'Nothing to list', details: [] });
    expect(await screen.findByText('Nothing to list')).toBeInTheDocument();
    expect(screen.queryByTestId('confirm-details')).not.toBeInTheDocument();
  });

  it('secondConfirm_cancelsTheStaleOne', async () => {
    render(<ConfirmHost />);
    const first = confirmDialog('first');
    const second = confirmDialog('second');
    await expect(first).resolves.toBe(false);
    expect(await screen.findByText('second')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'OK' }));
    await expect(second).resolves.toBe(true);
  });
});
