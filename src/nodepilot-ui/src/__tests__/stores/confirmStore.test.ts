import { describe, it, expect, beforeEach } from 'vitest';
import { useConfirmStore, confirmDialog } from '../../stores/confirmStore';

const settle = (ok: boolean) => useConfirmStore.getState().settle(ok);
const pending = () => useConfirmStore.getState().pending;

describe('confirmStore', () => {
  beforeEach(() => {
    // Drop any lingering pending confirm without resolving (fresh slate per test).
    useConfirmStore.setState({ pending: null });
  });

  it('confirmDialog stores a pending request with the given message', () => {
    void confirmDialog('Delete this?');
    expect(pending()?.message).toBe('Delete this?');
  });

  it('normalizes an object request and keeps its fields', () => {
    void confirmDialog({ message: 'Remove user', title: 'Danger', danger: true });
    const p = pending();
    expect(p?.message).toBe('Remove user');
    expect(p?.title).toBe('Danger');
    expect(p?.danger).toBe(true);
  });

  it('resolves true when the dialog is confirmed', async () => {
    const p = confirmDialog('proceed?');
    settle(true);
    await expect(p).resolves.toBe(true);
  });

  it('resolves false when the dialog is cancelled', async () => {
    const p = confirmDialog('proceed?');
    settle(false);
    await expect(p).resolves.toBe(false);
  });

  it('resets pending to null after resolution', () => {
    void confirmDialog('x');
    expect(pending()).not.toBeNull();
    settle(true);
    expect(pending()).toBeNull();
  });

  it('single-flight: opening a second confirm resolves the first promise with false', async () => {
    const first = confirmDialog('first');
    const second = confirmDialog('second'); // supersedes the first

    // The stale first confirm is auto-cancelled.
    await expect(first).resolves.toBe(false);

    // The live (second) confirm is now the pending one and still settles normally.
    expect(pending()?.message).toBe('second');
    settle(true);
    await expect(second).resolves.toBe(true);
  });

  it('settle on an empty store is a no-op (does not throw)', () => {
    expect(() => settle(true)).not.toThrow();
    expect(pending()).toBeNull();
  });
});
