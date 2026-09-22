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

describe('adminSettings error reporting', () => {
  afterEach(() => vi.restoreAllMocks());

  it('reportsTheServersOwnMessageWhenTheBodyCarriesOne', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      Response.json({ message: 'Secrets provider is not configured.' }, { status: 501 }),
    );

    await expect(adminSettings.getStatus()).rejects.toMatchObject({
      message: 'Secrets provider is not configured.',
      status: 501,
    });
  });

  it('fallsBackToTheStatusWhenTheBodyCarriesNoMessage', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      Response.json({}, { status: 500 }),
    );

    await expect(adminSettings.getStatus()).rejects.toMatchObject({
      message: 'Admin Settings API returned 500',
      status: 500,
    });
  });

  it('fallsBackToTheStatusWhenTheBodyIsBlankOrNotJson', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response('<html>gateway</html>', { status: 502 }),
    );

    await expect(adminSettings.getStatus()).rejects.toMatchObject({
      message: 'Admin Settings API returned 502',
      status: 502,
    });
  });

  it('treatsAWhitespaceOnlyMessageAsAbsent', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      Response.json({ message: '   ' }, { status: 400 }),
    );

    await expect(adminSettings.getStatus()).rejects.toMatchObject({
      message: 'Admin Settings API returned 400',
      status: 400,
    });
  });
});
