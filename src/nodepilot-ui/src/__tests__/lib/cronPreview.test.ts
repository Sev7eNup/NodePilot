import { describe, it, expect, vi, afterEach } from 'vitest';
import { relativeFromNow } from '../../lib/cronPreview';
import { normalizeQuartzCron, previewSchedule } from '../../../demo/run/schedulePreview';

describe('offline demo schedule normalization', () => {
  it('replacesQuestionMarkWildcardWithStar', () => {
    // Quartz uses ? in the day-of-month or day-of-week field as the not-specified marker.
    // cron-parser does not understand Quartz, so the marker is translated first.
    expect(normalizeQuartzCron('0 0 12 ? * MON-FRI')).toBe('0 0 12 * * MON-FRI');
    expect(normalizeQuartzCron('0 0 12 1 * ?')).toBe('0 0 12 1 * *');
  });

  it('does not silently discard a restrictive year', () => {
    expect(() => normalizeQuartzCron('0 0 12 * * ? 2026')).toThrow('NodePilot server');
    expect(normalizeQuartzCron('0 0 12 * * ? *')).toBe('0 0 12 * * *');
  });

  it('preservesValidSixFieldCron', () => {
    expect(normalizeQuartzCron('0 */5 * * * *')).toBe('0 */5 * * * *');
  });

  it('rejects short expressions that Quartz cannot execute', () => {
    expect(() => normalizeQuartzCron('0 2 * * *')).toThrow('six or seven');
    expect(() => normalizeQuartzCron('20 15 * *')).toThrow('six or seven');
  });

  it('translates the supported numeric weekday subset', () => {
    expect(normalizeQuartzCron('0 0 8 ? * 1')).toBe('0 0 8 * * 0');
    expect(normalizeQuartzCron('0 0 8 ? * 2-6')).toBe('0 0 8 * * 1-5');
    expect(() => normalizeQuartzCron('0 0 8 ? * MON#2')).toThrow('NodePilot server');
  });
});

describe('previewSchedule', () => {
  it('emptyCron_returnsErrorAndEmptyFires', () => {
    const result = previewSchedule('');
    expect(result.fireTimes).toEqual([]);
    expect(result.error).toContain('empty');
  });

  it('invalidCron_returnsErrorMessage', () => {
    const result = previewSchedule('not a cron');
    expect(result.fireTimes).toEqual([]);
    expect(result.error).not.toBeNull();
  });

  it('validCron_returnsRequestedFireCount', () => {
    const result = previewSchedule('0 */5 * * * ?', 5);
    expect(result.fireTimes).toHaveLength(5);
    expect(result.error).toBeNull();
  });

  it('fireTimes_areStrictlyAscending', () => {
    const { fireTimes } = previewSchedule('0 0 * * * ?', 5);
    for (let i = 1; i < fireTimes.length; i++) {
      expect(fireTimes[i].getTime()).toBeGreaterThan(fireTimes[i - 1].getTime());
    }
  });

  it('handlesQuartzQuestionMark_withoutThrowing', () => {
    // The UI lets users type the Quartz form with `?`, and the preview must still
    // render fire times for it.
    const result = previewSchedule('0 0 8 ? * MON-FRI');
    expect(result.error).toBeNull();
    expect(result.fireTimes.length).toBeGreaterThan(0);
  });

  it('never presents a Unix-only expression as an executable Quartz schedule', () => {
    expect(previewSchedule('0 2 * * *', 3)).toMatchObject({ fireTimes: [], error: expect.any(String) });
    expect(previewSchedule('0 0 2 * * ?', 3).fireTimes).toHaveLength(3);
  });

  it('fourFieldCron_returnsAnError', () => {
    const { fireTimes, error } = previewSchedule('20 15 * *', 3);

    expect(error).not.toBeNull();
    expect(fireTimes).toEqual([]);
  });
});

describe('relativeFromNow', () => {
  afterEach(() => vi.useRealTimers());

  it('pastTime_returnsNow', () => {
    expect(relativeFromNow(new Date(Date.now() - 5000))).toBe('now');
    expect(relativeFromNow(new Date(Date.now()))).toBe('now');
  });

  it('secondsAhead_returnsInSeconds', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-04-26T12:00:00Z'));
    const target = new Date('2026-04-26T12:00:30Z');
    expect(relativeFromNow(target)).toBe('in 30s');
  });

  it('minutesAhead_returnsMixedMinutesAndSeconds', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-04-26T12:00:00Z'));
    const target = new Date('2026-04-26T12:03:22Z');
    expect(relativeFromNow(target)).toBe('in 3m 22s');
  });

  it('hoursAhead_returnsMixedHoursAndMinutes', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-04-26T12:00:00Z'));
    const target = new Date('2026-04-26T14:15:00Z');
    expect(relativeFromNow(target)).toBe('in 2h 15m');
  });

  it('daysAhead_returnsMixedDaysAndHours', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-04-26T12:00:00Z'));
    const target = new Date('2026-04-29T18:00:00Z');
    expect(relativeFromNow(target)).toBe('in 3d 6h');
  });
});
