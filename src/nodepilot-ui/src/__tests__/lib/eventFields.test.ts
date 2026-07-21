import { describe, it, expect } from 'vitest';
import {
  customEventFields, EVENT_FIELD_CATALOG, NOTIFICATION_EVENT_TYPES, NOTIFICATION_CHANNELS,
} from '../../lib/eventFields';

describe('eventFields catalog', () => {
  it('NOTIFICATION_EVENT_TYPES mirrors the backend enum surface', () => {
    expect(NOTIFICATION_EVENT_TYPES).toContain('ExecutionFailed');
    expect(NOTIFICATION_EVENT_TYPES).toContain('ExecutionSucceeded');
    expect(NOTIFICATION_EVENT_TYPES).toContain('SystemAlert');
    // No duplicates.
    expect(new Set(NOTIFICATION_EVENT_TYPES).size).toBe(NOTIFICATION_EVENT_TYPES.length);
  });

  it('NOTIFICATION_CHANNELS exposes the delivery channels', () => {
    expect(NOTIFICATION_CHANNELS).toEqual(['Email', 'GenericWebhook']);
  });

  it('customEventFields returns the full field catalog', () => {
    const names = customEventFields().map((f) => f.name);
    expect(names).toEqual(EVENT_FIELD_CATALOG.map((f) => f.name));
    // Execution + shared fields plus the shared signal-populated ones are all offered.
    expect(names).toContain('workflowName');
    expect(names).toContain('eventType');
    expect(names).toContain('signalValue');
    expect(names).toContain('sourceKey');
    expect(names).toContain('targetMachine');
  });
});
