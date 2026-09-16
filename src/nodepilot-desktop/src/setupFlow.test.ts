import { describe, expect, it } from 'vitest';
import { appWindowFor, describeSetupFailure } from './setupFlow';

describe('appWindowFor', () => {
  it('opens the setup page while a setup handoff exists', () => {
    expect(appWindowFor(true)).toBe('setup');
  });

  it('opens the app once the handoff is gone', () => {
    expect(appWindowFor(false)).toBe('main');
  });
});

describe('describeSetupFailure', () => {
  it('drops a rejected handoff so the login page becomes reachable', () => {
    const failure = describeSetupFailure(401);
    expect(failure.discardHandoff).toBe(true);
    expect(failure.error).toMatch(/sign in with your existing account/);
  });

  it('keeps the handoff when only the password was rejected', () => {
    const failure = describeSetupFailure(400);
    expect(failure.discardHandoff).toBe(false);
    expect(failure.error).toMatch(/at least 8 characters/);
  });

  it('keeps the handoff on any other failure so the user can retry', () => {
    const failure = describeSetupFailure(503);
    expect(failure.discardHandoff).toBe(false);
    expect(failure.error).toContain('HTTP 503');
  });
});
