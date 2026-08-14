import { afterEach, describe, expect, it, vi } from 'vitest';
import { adminSettings } from '../../api/adminSettings';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

describe('adminSettings auth-boundary binding', () => {
  afterEach(() => vi.restoreAllMocks());

  it('discardsAStaleSuccessfulResponseBeforeReturningItToTheCaller', async () => {
    let resolveResponse!: (response: Response) => void;
    const pendingResponse = new Promise<Response>((resolve) => {
      resolveResponse = resolve;
    });
    vi.spyOn(globalThis, 'fetch').mockReturnValueOnce(pendingResponse);

    const staleRequest = adminSettings.getStatus();
    clearLocalAuthBoundary();
    resolveResponse(Response.json({
      overridesPath: 'user-a-path',
      restartRequired: false,
      restartRequiredSince: null,
      restartRequiredFor: [],
      lastSavedAt: null,
      lastSavedBy: 'user-a',
    }));

    await expect(staleRequest).rejects.toMatchObject({ name: 'AbortError' });
  });
});
