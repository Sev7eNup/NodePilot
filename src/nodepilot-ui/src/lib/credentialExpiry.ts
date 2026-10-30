import type { Credential } from '../types/api';

/** Mirror of the backend preset window (system policy `credential-expiring`: daysLeft <= 14). */
export const EXPIRY_WARN_DAYS = 14;

const DAY_MS = 86_400_000;

export type CredentialExpiry =
  | { kind: 'none' }
  | { kind: 'expired' }
  | { kind: 'expiring' | 'ok'; daysLeft: number };

/**
 * Classifies an expiry timestamp the way `CredentialExpirySource` does: whole days left rounded
 * down, expired once the timestamp is reached. Keeps the list chip and the sidebar badge in step
 * with the alert.
 */
export function credentialExpiryState(expiresAt: string | null, nowMs: number): CredentialExpiry {
  if (!expiresAt) return { kind: 'none' };
  const ms = new Date(expiresAt).getTime() - nowMs;
  if (Number.isNaN(ms)) return { kind: 'none' };
  if (ms <= 0) return { kind: 'expired' };
  const daysLeft = Math.floor(ms / DAY_MS);
  return { kind: daysLeft <= EXPIRY_WARN_DAYS ? 'expiring' : 'ok', daysLeft };
}

/** Number of credentials that are expired or inside the warn window. */
export function countCredentialsNeedingAttention(credentials: readonly Credential[], nowMs: number): number {
  return credentials.filter((c) => {
    const { kind } = credentialExpiryState(c.expiresAt, nowMs);
    return kind === 'expired' || kind === 'expiring';
  }).length;
}
