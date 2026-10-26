import { describe, expect, it } from 'vitest';
import {
  countCredentialsNeedingAttention,
  credentialExpiryState,
  EXPIRY_WARN_DAYS,
} from '../../lib/credentialExpiry';
import type { Credential } from '../../types/api';

const NOW = Date.parse('2026-10-24T12:00:00Z');
const DAY = 86_400_000;
const at = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();

describe('credentialExpiryState', () => {
  it('returnsNone_forMissingOrInvalidTimestamp', () => {
    expect(credentialExpiryState(null, NOW)).toEqual({ kind: 'none' });
    expect(credentialExpiryState('not-a-date', NOW)).toEqual({ kind: 'none' });
  });

  it('returnsExpired_onceTheTimestampIsReached', () => {
    expect(credentialExpiryState(at(0), NOW)).toEqual({ kind: 'expired' });
    expect(credentialExpiryState(at(-DAY), NOW)).toEqual({ kind: 'expired' });
  });

  it('roundsDaysDown_likeTheBackendAlert', () => {
    // 14.5 days left: the alert preset (floor <= 14) fires, so the chip must warn too.
    expect(credentialExpiryState(at(14.5 * DAY), NOW)).toEqual({ kind: 'expiring', daysLeft: 14 });
    expect(credentialExpiryState(at(0.5 * DAY), NOW)).toEqual({ kind: 'expiring', daysLeft: 0 });
  });

  it('warnsUpToTheWindowEdge_andNotBeyond', () => {
    expect(credentialExpiryState(at(EXPIRY_WARN_DAYS * DAY), NOW))
      .toEqual({ kind: 'expiring', daysLeft: EXPIRY_WARN_DAYS });
    expect(credentialExpiryState(at((EXPIRY_WARN_DAYS + 1) * DAY), NOW))
      .toEqual({ kind: 'ok', daysLeft: EXPIRY_WARN_DAYS + 1 });
  });
});

describe('countCredentialsNeedingAttention', () => {
  it('countsExpiredAndExpiring_only', () => {
    const credential = (id: string, expiresAt: string | null): Credential =>
      ({ id, name: id, username: 'svc', domain: null, expiresAt });
    const rows = [
      credential('expired', at(-DAY)),
      credential('expiring', at(5 * DAY)),
      credential('later', at(100 * DAY)),
      credential('never', null),
    ];
    expect(countCredentialsNeedingAttention(rows, NOW)).toBe(2);
    expect(countCredentialsNeedingAttention([], NOW)).toBe(0);
  });
});
