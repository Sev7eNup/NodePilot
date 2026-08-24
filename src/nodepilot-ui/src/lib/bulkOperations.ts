import {
  AuthBoundaryChangedError,
  assertAuthBoundaryGenerationCurrent,
  isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';

export interface BulkFailure<T> {
  item: T;
  message: string;
}

export interface BulkResult<T> {
  succeeded: T[];
  /** Items the operation reported as "nothing to do" (e.g. a move into the folder it already sits in). */
  skipped: T[];
  failed: BulkFailure<T>[];
  /** True when `shouldAbort` stopped the run before every item was processed. */
  aborted: boolean;
}

export interface BulkProgress {
  done: number;
  total: number;
  /** Label of the item currently being processed. */
  current: string;
}

export interface RunBulkOptions<T> {
  getLabel: (item: T) => string;
  /** Generation captured before the run — every iteration re-checks it. */
  authBoundaryGeneration: number;
  onProgress?: (progress: BulkProgress) => void;
  /** Polled before each item; returning true stops the run and sets `aborted`. */
  shouldAbort?: () => boolean;
}

/** Thrown by an operation to record the item as skipped instead of succeeded or failed. */
export class BulkSkippedError extends Error {
  constructor(message = 'skipped') {
    super(message);
    this.name = 'BulkSkippedError';
  }
}

/**
 * Runs `op` over `items` one at a time and reports a per-item outcome.
 *
 * Sequential on purpose. Firing N deletes at once would put N audit writes and N RBAC checks on
 * the server simultaneously for what is a housekeeping action, and neither progress nor a cancel
 * button could exist. This mirrors the multi-file import loop in WorkflowsPage, including its two
 * hard rules:
 *
 *  - A failing item never aborts the batch. Someone clearing 30 workflows wants the other 29 gone
 *    plus a report naming the one that refused, not an all-or-nothing stop.
 *  - The auth boundary is re-checked before and after every call. Continuing the loop after a
 *    user switch would run the rest of User A's batch under User B's cookie, so that case throws
 *    out of the whole run rather than being recorded as a per-item failure.
 */
export async function runBulkOperation<T>(
  items: readonly T[],
  op: (item: T) => Promise<void>,
  options: RunBulkOptions<T>,
): Promise<BulkResult<T>> {
  const { getLabel, authBoundaryGeneration, onProgress, shouldAbort } = options;
  const result: BulkResult<T> = { succeeded: [], skipped: [], failed: [], aborted: false };

  for (const [index, item] of items.entries()) {
    if (shouldAbort?.()) {
      result.aborted = true;
      break;
    }
    assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
    onProgress?.({ done: index, total: items.length, current: getLabel(item) });

    try {
      await op(item);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      result.succeeded.push(item);
    } catch (err) {
      if (err instanceof BulkSkippedError) {
        result.skipped.push(item);
        continue;
      }
      // Boundary aborts are control flow, not a per-item outcome.
      if (err instanceof AuthBoundaryChangedError) throw err;
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) throw new AuthBoundaryChangedError();
      result.failed.push({ item, message: (err as Error).message });
    }
  }

  if (!result.aborted) {
    onProgress?.({ done: items.length, total: items.length, current: '' });
  }
  return result;
}
