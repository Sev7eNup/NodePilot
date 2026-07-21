import { describe, it, expect, vi, afterEach } from 'vitest';
import { normalizeQuartzCron, previewSchedule, relativeFromNow } from '../../lib/cronPreview';

describe('normalizeQuartzCron', () => {
  it('replacesQuestionMarkWildcardWithStar', () => {
    // Quartz cron uses ? in dom OR dow as the "not specified" marker. cron-parser
    // doesn't speak Quartz, so we have to translate.
    expect(normalizeQuartzCron('0 0 12 ? * MON-FRI')).toBe('0 0 12 * * MON-FRI');
    expect(normalizeQuartzCron('0 0 12 1 * ?')).toBe('0 0 12 1 * *');
  });

  it('truncatesYearField_byDroppingTheSeventhField', () => {
    // Quartz allows an optional 7th "year" field. cron-parser doesn't; we just drop it.
    expect(normalizeQuartzCron('0 0 12 * * ? 2026')).toBe('0 0 12 * * *');
  });

  it('preservesValidSixFieldCron', () => {
    expect(normalizeQuartzCron('0 */5 * * * *')).toBe('0 */5 * * * *');
  });

  it('emptyInput_returnsEmpty', () => {
    expect(normalizeQuartzCron('')).toBe('');
    expect(normalizeQuartzCron('   ')).toBe('');
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
    // The whole reason normalizeQuartzCron exists — the UI lets users type the
    // Quartz form with `?`, the preview must still render fires.
    const result = previewSchedule('0 0 8 ? * MON-FRI');
    expect(result.error).toBeNull();
    expect(result.fireTimes.length).toBeGreaterThan(0);
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
