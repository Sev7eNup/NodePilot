import { describe, it, expect, vi, afterEach } from 'vitest';
import { normalizeQuartzCron, previewSchedule, relativeFromNow } from '../../lib/cronPreview';

describe('normalizeQuartzCron', () => {
  it('replacesQuestionMarkWildcardWithStar', () => {
    // Quartz uses ? in the day-of-month or day-of-week field as the not-specified marker.
    // cron-parser does not understand Quartz, so the marker is translated first.
    expect(normalizeQuartzCron('0 0 12 ? * MON-FRI')).toBe('0 0 12 * * MON-FRI');
    expect(normalizeQuartzCron('0 0 12 1 * ?')).toBe('0 0 12 1 * *');
  });

  it('truncatesYearField_byDroppingTheSeventhField', () => {
    // Quartz allows an optional seventh year field; cron-parser does not, so it is dropped.
    expect(normalizeQuartzCron('0 0 12 * * ? 2026')).toBe('0 0 12 * * *');
  });

  it('preservesValidSixFieldCron', () => {
    expect(normalizeQuartzCron('0 */5 * * * *')).toBe('0 */5 * * * *');
  });

  it('leavesShortExpressionsUnpadded_paddingIsTheParsersJob', () => {
    // The normalizer only truncates and translates `?`. Padding a short expression to six
    // fields belongs to cron-parser, which prepends the missing leading fields, so a
    // five-field Unix cron keeps reading minute-first. Padding here would shift every
    // field by one.
    expect(normalizeQuartzCron('0 2 * * *')).toBe('0 2 * * *');
    expect(normalizeQuartzCron('20 15 * *')).toBe('20 15 * *');
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
    // The UI lets users type the Quartz form with `?`, and the preview must still
    // render fire times for it.
    const result = previewSchedule('0 0 8 ? * MON-FRI');
    expect(result.error).toBeNull();
    expect(result.fireTimes.length).toBeGreaterThan(0);
  });

  // ── Short expressions ──────────────────────────────────────────────────────
  // The designer accepts free text, so the preview also receives five-field Unix cron and
  // four-field typos. Neither form is Quartz, and both must be padded from the leading end
  // so the seconds default lands first rather than last.
  //
  // Both cases assert relative spacing rather than wall-clock times: previewSchedule reads
  // `new Date()` internally, so there is no base date to pin.

  it('fiveFieldUnixCron_readsMinuteFirst_matchingItsSixFieldEquivalent', () => {
    // `0 2 * * *` (Unix) and `0 0 2 * * *` (Quartz) are the same schedule: 02:00 daily.
    // Padding from the trailing end would collapse the five-field form to second 0 of
    // minute 2 of every hour, and the two would diverge.
    const short = previewSchedule('0 2 * * *', 3);
    const long = previewSchedule('0 0 2 * * *', 3);

    expect(short.error).toBeNull();
    expect(short.fireTimes.map((d) => d.getTime())).toEqual(long.fireTimes.map((d) => d.getTime()));
  });

  it('fourFieldCron_doesNotCollapseToEverySecond', () => {
    // Padding `20 15 * *` from the trailing end would make it fire once per second. A
    // preview claiming a per-second schedule for a daily job is worse than an error.
    const { fireTimes, error } = previewSchedule('20 15 * *', 3);

    expect(error).toBeNull();
    expect(fireTimes.length).toBe(3);
    for (let i = 1; i < fireTimes.length; i++) {
      const gapSeconds = (fireTimes[i].getTime() - fireTimes[i - 1].getTime()) / 1000;
      expect(gapSeconds).toBeGreaterThanOrEqual(60);
    }
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
