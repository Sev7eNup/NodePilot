import { describe, expect, it } from 'vitest';
import type { AppLogEntry } from '../../api/diagnostics';
import { areaOf, groupKey, groupProblems, normalizeMessage } from '../../lib/appLog';

function entry(over: Partial<AppLogEntry>): AppLogEntry {
  return {
    timestamp: '2026-10-12T10:00:00Z',
    level: 'Warning',
    source: 'NodePilot.Engine.Execution.StepRunner',
    message: 'something happened',
    template: null,
    details: null,
    truncated: false,
    format: 'cmtrace',
    ...over,
  };
}

describe('areaOf', () => {
  it.each([
    ['NodePilot.Engine.WorkflowEngine', 'engine'],
    ['Npgsql.Connection', 'database'],
    ['Microsoft.EntityFrameworkCore.Database.Command', 'database'],
    ['NodePilot.Scheduler.TriggerOrchestrator', 'scheduler'],
    ['NodePilot.Remote.Winrm.WinRmClient', 'remote'],
    ['NodePilot.Ai.LlmClient', 'ai'],
    ['NodePilot.Api.Security.Ldap.LdapAuthenticator', 'auth'],
    ['Microsoft.AspNetCore.Hosting.Diagnostics', 'web'],
    ['Some.Other.Thing', 'other'],
  ])('maps %s to %s', (source, area) => {
    expect(areaOf(source)).toBe(area);
  });

  it('prefers auth over the generic NodePilot.Api namespace', () => {
    expect(areaOf('NodePilot.Api.Security.Scim.ScimProvisioningService')).toBe('auth');
  });
});

describe('normalizeMessage', () => {
  it('folds values that differ per occurrence', () => {
    const a = normalizeMessage('Execution 6f1c2a3e-1111-4222-8333-444455556666 took 1520 ms at 2026-10-12T10:00:00Z');
    const b = normalizeMessage('Execution aaaaaaaa-1111-4222-8333-444455556666 took 87 ms at 2026-10-12T11:30:05Z');
    expect(a).toBe(b);
  });

  it('keeps short numbers and codes so different causes stay apart', () => {
    expect(normalizeMessage('HTTP 401 from host')).not.toBe(normalizeMessage('HTTP 500 from host'));
    expect(normalizeMessage('failed with 0x80070005')).toContain('0x80070005');
    expect(normalizeMessage('SQLSTATE 28P01 on port 5432')).toBe('SQLSTATE 28P01 on port 5432');
  });
});

describe('groupKey', () => {
  it('separates the same text from different sources or levels', () => {
    const base = entry({ message: 'connection failed' });
    expect(groupKey(base)).not.toBe(groupKey(entry({ message: 'connection failed', source: 'Npgsql.Connection' })));
    expect(groupKey(base)).not.toBe(groupKey(entry({ message: 'connection failed', level: 'Error' })));
  });

  it('uses the message template without further normalisation when there is one', () => {
    const a = entry({ template: 'Step {StepId} failed', message: 'Step 12345678 failed' });
    const b = entry({ template: 'Step {StepId} failed', message: 'Step other failed' });
    expect(groupKey(a)).toBe(groupKey(b));
  });
});

describe('groupProblems', () => {
  it('counts occurrences, keeps the newest as latest and ignores info entries', () => {
    const groups = groupProblems([
      entry({ timestamp: '2026-10-12T10:05:00Z', message: 'Retry 1 of host a took 100 ms' }),
      entry({ timestamp: '2026-10-12T10:01:00Z', message: 'Retry 1 of host a took 250 ms' }),
      entry({ timestamp: '2026-10-12T10:03:00Z', level: 'Information', message: 'all good' }),
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      count: 2, firstSeen: '2026-10-12T10:01:00Z', lastSeen: '2026-10-12T10:05:00Z', area: 'engine',
    });
    expect(groups[0].latest.message).toContain('100 ms');
  });

  it('lists errors before warnings and then the most recent first', () => {
    const groups = groupProblems([
      entry({ timestamp: '2026-10-12T12:00:00Z', message: 'late warning' }),
      entry({ timestamp: '2026-10-12T09:00:00Z', level: 'Error', message: 'early error' }),
      entry({ timestamp: '2026-10-12T11:00:00Z', message: 'earlier warning' }),
    ]);

    expect(groups.map((g) => g.latest.message)).toEqual(['early error', 'late warning', 'earlier warning']);
  });
});
