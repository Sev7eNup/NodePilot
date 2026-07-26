import type { BrowserWindow, Session } from 'electron';
import { describe, expect, it, vi } from 'vitest';

import { hardenSession, hardenWindow } from './security';

/**
 * A self-signed leaf generated once for this suite (CN=localhost, valid until 2126). The shell
 * pins by SHA-256 fingerprint, so the test needs a real certificate whose fingerprint is known —
 * not a stub — otherwise the comparison under test is never actually exercised.
 */
const TEST_CERT_PEM = `-----BEGIN CERTIFICATE-----
MIIDCzCCAfOgAwIBAgIUX5825i1yJZHLx86kFxXxNiFAK9owDQYJKoZIhvcNAQEL
BQAwFDESMBAGA1UEAwwJbG9jYWxob3N0MCAXDTI2MDcyNjA5MTYwM1oYDzIxMjYw
NzAyMDkxNjAzWjAUMRIwEAYDVQQDDAlsb2NhbGhvc3QwggEiMA0GCSqGSIb3DQEB
AQUAA4IBDwAwggEKAoIBAQCXqRzvFcLfLX6xaLhJ+0SmXHNLB3J8aTyVOkDzs/MW
MFI/kb65nO+7LRspsWZfDSDE7fbkv8Z+2CYAZ/x8EL/8duZperygQSALNhUQSkpm
SHLD37XYi0mDAi/aMzzjcyELmyKSpPM3xAzeaH03N5k/BQn3TYYlR2fnVRLAEym0
DIK3iDK7ZtIBYhUiOoGYIjVxMj2tgRmIHjIrQZUregWZ0LTX6IFgjYE4pk1I2qZV
BSGLAb3C5S15e3rJPoxB02a/ktkIyIdPYrMHTBaUCAn0m9OW80bFYxpv4OlRj7HO
P040ZzoXF9dAWO6qrcYg377n1V/zdF6lzJhMQzG3H2jbAgMBAAGjUzBRMB0GA1Ud
DgQWBBRTD09YtRZQwAP8xbjEA2elfL0TdTAfBgNVHSMEGDAWgBRTD09YtRZQwAP8
xbjEA2elfL0TdTAPBgNVHRMBAf8EBTADAQH/MA0GCSqGSIb3DQEBCwUAA4IBAQCQ
xUGpD5S+dtw9UpiPodfk25hqItCCEKSFlJWYcvsrm+as91Uy5vg9T7I8O+7dyAUf
yJ7upU8/rSNDYBSzzzeiFZIT3f1qKzcmv9WmdM+mcxJsXUiV+wENjGJn4uZ/gEH0
ON3QOcrW+F401HjCDZp2Ejf2GRsEzJxh5YCBIAHJY/homu8wcAN9Vx3ZzKSBS6bx
H2dYUjB8dToEsCsXYCPj5Rm5u/W/HeO/OKX1oua6JXIEYrOLA0fEZZbBq/ZJ9Bjf
y0nw4EYJiRTpImTSa00VHzfMnHttBohh4NwQLrnDT7lxim/hAPyl4PJicVoPsoUd
WrHhbFEEm4tFKZSLIXHh
-----END CERTIFICATE-----
`;

const TEST_CERT_SHA256 = 'A820B18F3E85D42B0BA1D6BA06932DC06EF0CB1A1397766048ECDC68F8E5F4AC';

/** Electron verdicts: 0 = trust this certificate, -2 = reject, -3 = use Chromium's own result. */
const TRUST = 0;
const REJECT = -2;
const DEFER_TO_CHROMIUM = -3;

type VerifyProc = (
  request: { hostname: string; certificate: { data: string } },
  callback: (verdict: number) => void,
) => void;

function fakeSession() {
  let verifyProc: VerifyProc | undefined;
  const listeners = new Map<string, (event: { preventDefault: () => void }) => void>();

  const sess = {
    setCertificateVerifyProc: vi.fn((proc: VerifyProc) => { verifyProc = proc; }),
    setPermissionRequestHandler: vi.fn(),
    setPermissionCheckHandler: vi.fn(),
    on: vi.fn((event: string, handler: (e: { preventDefault: () => void }) => void) => {
      listeners.set(event, handler);
    }),
  };

  const verify = (hostname: string, pem: string): number => {
    let verdict: number | undefined;
    verifyProc!({ hostname, certificate: { data: pem } }, (v) => { verdict = v; });
    return verdict!;
  };

  return { sess: sess as unknown as Session, raw: sess, verify, listeners };
}

describe('hardenSession — certificate pinning', () => {
  it('trusts the loopback certificate whose fingerprint matches the pin', () => {
    const { sess, verify } = fakeSession();
    hardenSession(sess, TEST_CERT_SHA256);

    expect(verify('localhost', TEST_CERT_PEM)).toBe(TRUST);
    expect(verify('127.0.0.1', TEST_CERT_PEM)).toBe(TRUST);
  });

  it('rejects a loopback certificate whose fingerprint differs from the pin', () => {
    const { sess, verify } = fakeSession();
    hardenSession(sess, 'B'.repeat(64));

    expect(verify('localhost', TEST_CERT_PEM)).toBe(REJECT);
  });

  it('rejects when the certificate cannot be parsed', () => {
    const { sess, verify } = fakeSession();
    hardenSession(sess, TEST_CERT_SHA256);

    expect(verify('localhost', 'not a certificate')).toBe(REJECT);
  });

  it('defers to Chromium for any non-loopback host', () => {
    // Nothing else should ever be contacted; a matching fingerprint must not buy trust here.
    const { sess, verify } = fakeSession();
    hardenSession(sess, TEST_CERT_SHA256);

    expect(verify('evil.example.com', TEST_CERT_PEM)).toBe(DEFER_TO_CHROMIUM);
    expect(verify('localhost.evil.example.com', TEST_CERT_PEM)).toBe(DEFER_TO_CHROMIUM);
  });

  it('denies every permission request and check', () => {
    const { sess, raw } = fakeSession();
    hardenSession(sess, TEST_CERT_SHA256);

    const requestHandler = raw.setPermissionRequestHandler.mock.calls[0][0] as
      (wc: unknown, permission: string, done: (granted: boolean) => void) => void;
    const done = vi.fn();
    requestHandler(null, 'media', done);
    expect(done).toHaveBeenCalledWith(false);

    const checkHandler = raw.setPermissionCheckHandler.mock.calls[0][0] as () => boolean;
    expect(checkHandler()).toBe(false);
  });

  it('blocks downloads', () => {
    const { sess, listeners } = fakeSession();
    hardenSession(sess, TEST_CERT_SHA256);

    const preventDefault = vi.fn();
    listeners.get('will-download')!({ preventDefault });
    expect(preventDefault).toHaveBeenCalled();
  });
});

describe('hardenWindow — navigation containment', () => {
  const ORIGIN = 'https://localhost:5001';

  function fakeWindow() {
    const handlers = new Map<string, (event: { preventDefault: () => void }, url: string) => void>();
    let openHandler: (() => { action: string }) | undefined;

    const win = {
      webContents: {
        on: vi.fn((event: string, handler: (e: { preventDefault: () => void }, url: string) => void) => {
          handlers.set(event, handler);
        }),
        setWindowOpenHandler: vi.fn((handler: () => { action: string }) => { openHandler = handler; }),
      },
    };

    const navigate = (event: 'will-navigate' | 'will-redirect', url: string): boolean => {
      const preventDefault = vi.fn();
      handlers.get(event)!({ preventDefault }, url);
      return preventDefault.mock.calls.length === 0; // true = navigation allowed
    };

    return { win: win as unknown as BrowserWindow, navigate, openHandler: () => openHandler! };
  }

  it.each(['will-navigate', 'will-redirect'] as const)('allows same-origin %s', (event) => {
    const { win, navigate } = fakeWindow();
    hardenWindow(win, ORIGIN);

    expect(navigate(event, `${ORIGIN}/workflows/123`)).toBe(true);
  });

  it.each([
    ['a different host', 'https://evil.example.com/'],
    ['a different scheme', 'http://localhost:5001/'],
    ['a different port', 'https://localhost:5002/'],
    ['a file URL', 'file:///C:/payload.html'],
    ['an unparsable URL', 'javascript:alert(1)'],
  ])('blocks navigation to %s', (_label, url) => {
    const { win, navigate } = fakeWindow();
    hardenWindow(win, ORIGIN);

    expect(navigate('will-navigate', url)).toBe(false);
    expect(navigate('will-redirect', url)).toBe(false);
  });

  it('denies every window.open / popup', () => {
    const { win, openHandler } = fakeWindow();
    hardenWindow(win, ORIGIN);

    expect(openHandler()()).toEqual({ action: 'deny' });
  });
});
