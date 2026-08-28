import { describe, it, expect, afterEach, vi } from 'vitest';
import { randomUuid } from '../../lib/uuid';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('randomUuid', () => {
  it('delegates to crypto.randomUUID when the platform offers it', () => {
    const spy = vi
      .spyOn(globalThis.crypto, 'randomUUID')
      .mockReturnValue('11111111-2222-4333-8444-555555555555');

    expect(randomUuid()).toBe('11111111-2222-4333-8444-555555555555');
    expect(spy).toHaveBeenCalledTimes(1);
  });

  // Over plain HTTP on a LAN address the page is not a secure context, so
  // `crypto.randomUUID` is unavailable. randomUuid must still produce a valid v4 without it.
  it('produces a valid v4 without crypto.randomUUID (insecure context)', () => {
    vi.stubGlobal('crypto', { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });

    const id = randomUuid();

    expect(id).toMatch(UUID_V4);
  });

  it('still produces a valid v4 without WebCrypto at all', () => {
    vi.stubGlobal('crypto', undefined);

    expect(randomUuid()).toMatch(UUID_V4);
  });

  it('does not repeat itself across calls in an insecure context', () => {
    vi.stubGlobal('crypto', { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });

    const ids = new Set(Array.from({ length: 200 }, () => randomUuid()));

    expect(ids.size).toBe(200);
  });
});
