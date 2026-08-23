/**
 * A random UUID v4 that also works outside a secure context.
 *
 * `crypto.randomUUID` is **secure-context-only**: it exists on `https://` and on
 * `http://localhost`, but is simply absent when the app is opened over a plain-HTTP LAN
 * address — e.g. a phone pointed at the dev server on `http://192.168.x.x:5173`. Every
 * unguarded call site then threw `crypto.randomUUID is not a function` and took the whole
 * route down (the AI chat page crashed on mount, the designer on the first node insert).
 *
 * `crypto.getRandomValues` carries no such gate, so the fallback stays CSPRNG-backed and
 * produces a spec-shaped v4. `Math.random` is the last resort for an environment without
 * WebCrypto at all. Everything generated here is a collision handle (node/edge/thread id),
 * never a credential — but there is also no reason to hand out weak ids when good ones are
 * one call away.
 */
export function randomUuid(): string {
  const webCrypto = globalThis.crypto as Crypto | undefined;
  if (typeof webCrypto?.randomUUID === 'function') return webCrypto.randomUUID();

  const bytes = new Uint8Array(16);
  if (typeof webCrypto?.getRandomValues === 'function') {
    webCrypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 10xx

  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
