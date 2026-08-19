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

  it('leavesShortExpressionsUnpadded_paddingIsTheParsersJob', () => {
    // Deliberate: the normalizer only truncates and translates `?`. Padding a short
    // expression to six fields is cron-parser's business, and it prepends the missing
    // leading fields — so a 5-field Unix cron keeps reading minute-first, not
    // seconds-first. Pinning that here means a normalizer that starts padding on its
    // own (and would shift every field by one) fails loudly.
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
    // The whole reason normalizeQuartzCron exists — the UI lets users type the
    // Quartz form with `?`, the preview must still render fires.
    const result = previewSchedule('0 0 8 ? * MON-FRI');
    expect(result.error).toBeNull();
    expect(result.fireTimes.length).toBeGreaterThan(0);
  });

  // ── Short expressions ──────────────────────────────────────────────────────
  // The designer accepts free text, so a user who knows standard Unix cron types five
  // fields, and a typo leaves four. Neither form is Quartz, but both reach the preview,
  // and until cron-parser 5.10.0 the shorter ones were padded from the wrong end: the
  // seconds default landed last instead of first, so `20 15 * *` previewed as "every
  // second". Every other case in this file uses six or seven fields, which is exactly
  // why that stayed invisible. These two pin the behaviour the preview depends on.
  //
  // Both assert *relative* spacing rather than wall-clock times: previewSchedule reads
  // `new Date()` internally, so there is no base date to pin.

  it('fiveFieldUnixCron_readsMinuteFirst_matchingItsSixFieldEquivalent', () => {
    // `0 2 * * *` (Unix) and `0 0 2 * * *` (Quartz) are the same schedule: 02:00 daily.
    // If the parser ever padded from the wrong end, the five-field form would collapse
    // to "second 0 of minute 2 of every hour" and the two would diverge.
    const short = previewSchedule('0 2 * * *', 3);
    const long = previewSchedule('0 0 2 * * *', 3);

    expect(short.error).toBeNull();
    expect(short.fireTimes.map((d) => d.getTime())).toEqual(long.fireTimes.map((d) => d.getTime()));
  });

  it('fourFieldCron_doesNotCollapseToEverySecond', () => {
    // The regression this guards: pre-5.10.0 cron-parser padded `20 15 * *` such that
    // it fired once per second. A preview claiming a per-second schedule for what the
    // user meant as a daily job is worse than an error message.
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
