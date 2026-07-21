import { describe, it, expect } from 'vitest';
import { ACTIVITY_TYPES, EXTERNAL_TRIGGER_TYPES } from '../../lib/activityTypes';

/**
 * ACTIVITY_TYPES is a thin aliases map but the values are load-bearing — the C# executor
 * registry uses the same string literals as keys. Pin a few critical ones so a
 * "rename in TS" refactor can't silently break the engine binding.
 */

describe('ACTIVITY_TYPES', () => {
  it('keysAndValuesMatchEngineBinding', () => {
    expect(ACTIVITY_TYPES.RUN_SCRIPT).toBe('runScript');
    expect(ACTIVITY_TYPES.REST_API).toBe('restApi');
    expect(ACTIVITY_TYPES.MANUAL_TRIGGER).toBe('manualTrigger');
    expect(ACTIVITY_TYPES.SCHEDULE_TRIGGER).toBe('scheduleTrigger');
    expect(ACTIVITY_TYPES.WEBHOOK_TRIGGER).toBe('webhookTrigger');
  });

  it('allValuesAreCamelCaseStrings', () => {
    // The convention is camelCase; UPPER_SNAKE_CASE in the map → camelCase in the engine.
    for (const [k, v] of Object.entries(ACTIVITY_TYPES)) {
      expect(typeof v, `value of ${k}`).toBe('string');
      expect(v, `value of ${k} should be camelCase`).toMatch(/^[a-z][a-zA-Z0-9]*$/);
    }
  });

  it('allValuesAreUnique', () => {
    const values = Object.values(ACTIVITY_TYPES);
    expect(new Set(values).size).toBe(values.length);
  });

  it('coversTheCoreActivityFamilies', () => {
    // Sanity: the catalog must contain at least these five conceptual buckets so the
    // designer doesn't render a half-empty palette.
    const values = new Set(Object.values(ACTIVITY_TYPES));
    expect(values.has('runScript')).toBe(true); // remote execution
    expect(values.has('restApi')).toBe(true);   // engine-local IO
    expect(values.has('junction')).toBe(true);  // control flow
    expect(values.has('returnData')).toBe(true);
    expect(values.has('webhookTrigger')).toBe(true); // trigger
  });
});

describe('EXTERNAL_TRIGGER_TYPES', () => {
  it('containsExactlyTheFiveBackgroundTriggers', () => {
    expect([...EXTERNAL_TRIGGER_TYPES].sort()).toEqual([
      'databaseTrigger',
      'eventLogTrigger',
      'fileWatcherTrigger',
      'scheduleTrigger',
      'webhookTrigger',
    ]);
  });

  it('excludesManualTrigger', () => {
    // manualTrigger is fired by the UI/API, not by the TriggerOrchestrator — it must
    // never appear in this list, otherwise the orchestrator would try to spin up a
    // never-firing source for it.
    expect(EXTERNAL_TRIGGER_TYPES).not.toContain('manualTrigger');
  });

  it('everyExternalTriggerEntryExistsInActivityTypes', () => {
    const known = new Set(Object.values(ACTIVITY_TYPES));
    for (const t of EXTERNAL_TRIGGER_TYPES) {
      expect(known.has(t)).toBe(true);
    }
  });
});
