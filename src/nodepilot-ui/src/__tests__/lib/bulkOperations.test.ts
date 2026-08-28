import { describe, it, expect, vi, afterEach } from 'vitest';
import { runBulkOperation, BulkSkippedError, type BulkProgress } from '../../lib/bulkOperations';
import {
  AuthBoundaryChangedError,
  captureAuthBoundaryGeneration,
  clearLocalAuthBoundary,
} from '../../security/authBoundary';

type Item = { id: string; name: string };
const items = (...ids: string[]): Item[] => ids.map((id) => ({ id, name: id.toUpperCase() }));
const opts = (extra: Partial<Parameters<typeof runBulkOperation<Item>>[2]> = {}) => ({
  getLabel: (i: Item) => i.name,
  authBoundaryGeneration: captureAuthBoundaryGeneration(),
  ...extra,
});

afterEach(() => clearLocalAuthBoundary());

describe('runBulkOperation', () => {
  it('runs every item and reports them as succeeded', async () => {
    const seen: string[] = [];
    const result = await runBulkOperation(items('a', 'b', 'c'), async (i) => { seen.push(i.id); }, opts());

    expect(seen).toEqual(['a', 'b', 'c']);
    expect(result.succeeded.map((i) => i.id)).toEqual(['a', 'b', 'c']);
    expect(result.failed).toEqual([]);
    expect(result.aborted).toBe(false);
  });

  it('runs strictly sequentially', async () => {
    const events: string[] = [];
    await runBulkOperation(items('a', 'b'), async (i) => {
      events.push(`start-${i.id}`);
      await Promise.resolve();
      events.push(`end-${i.id}`);
    }, opts());

    expect(events).toEqual(['start-a', 'end-a', 'start-b', 'end-b']);
  });

  // A single failing item must not cost the user the rest of the batch.
  it('a failing item does not stop the run', async () => {
    const result = await runBulkOperation(items('a', 'b', 'c'), async (i) => {
      if (i.id === 'b') throw new Error('423 Locked');
    }, opts());

    expect(result.succeeded.map((i) => i.id)).toEqual(['a', 'c']);
    expect(result.failed).toHaveLength(1);
    expect(result.failed[0].item.id).toBe('b');
    expect(result.failed[0].message).toBe('423 Locked');
  });

  it('records BulkSkippedError as skipped, not as a failure', async () => {
    const result = await runBulkOperation(items('a', 'b'), async (i) => {
      if (i.id === 'a') throw new BulkSkippedError();
    }, opts());

    expect(result.skipped.map((i) => i.id)).toEqual(['a']);
    expect(result.succeeded.map((i) => i.id)).toEqual(['b']);
    expect(result.failed).toEqual([]);
  });

  it('reports progress before each item and once at the end', async () => {
    const seen: BulkProgress[] = [];
    await runBulkOperation(items('a', 'b'), async () => {}, opts({ onProgress: (p) => seen.push({ ...p }) }));

    expect(seen).toEqual([
      { done: 0, total: 2, current: 'A' },
      { done: 1, total: 2, current: 'B' },
      { done: 2, total: 2, current: '' },
    ]);
  });

  it('shouldAbort stops the run and flags the result', async () => {
    const seen: string[] = [];
    let stop = false;
    const result = await runBulkOperation(items('a', 'b', 'c'), async (i) => {
      seen.push(i.id);
      if (i.id === 'b') stop = true;
    }, opts({ shouldAbort: () => stop }));

    expect(seen).toEqual(['a', 'b']);
    expect(result.aborted).toBe(true);
    expect(result.succeeded.map((i) => i.id)).toEqual(['a', 'b']);
  });

  it('an empty item list is a no-op success', async () => {
    const op = vi.fn();
    const result = await runBulkOperation([], op, opts());
    expect(op).not.toHaveBeenCalled();
    expect(result).toEqual({ succeeded: [], skipped: [], failed: [], aborted: false });
  });

  // Continuing after a user switch would run the rest of the batch under the new user's
  // cookie, so the whole run throws instead of recording a failure.
  it('throws when the auth boundary moves mid-run instead of recording a failure', async () => {
    const seen: string[] = [];
    await expect(runBulkOperation(items('a', 'b', 'c'), async (i) => {
      seen.push(i.id);
      if (i.id === 'a') clearLocalAuthBoundary();
    }, opts())).rejects.toBeInstanceOf(AuthBoundaryChangedError);

    expect(seen).toEqual(['a']);
  });

  it('propagates an AuthBoundaryChangedError thrown by the operation itself', async () => {
    await expect(runBulkOperation(items('a'), async () => {
      throw new AuthBoundaryChangedError();
    }, opts())).rejects.toBeInstanceOf(AuthBoundaryChangedError);
  });
});
