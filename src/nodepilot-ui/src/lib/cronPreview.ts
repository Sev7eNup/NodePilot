import { CronExpressionParser } from 'cron-parser';
import i18n from '../i18n';

/**
 * Normalizes a Quartz cron expression (7 fields) into the 6-field form the cron-parser library
 * accepts. Quartz uses `?` as an unspecified placeholder on day-of-month or day-of-week, while
 * cron-parser needs a real range in every field, so `?` becomes `*`. The year field is dropped
 * because cron-parser projects future fire times without a year bound.
 */
export function normalizeQuartzCron(cron: string): string {
  const trimmed = cron.trim();
  if (!trimmed) return trimmed;
  const parts = trimmed.split(/\s+/);
  // At most 7 fields (seconds+minutes+hours+dom+month+dow+year); drop the year field.
  const sixOrLess = parts.slice(0, 6);
  // Replace ? with * for day-of-month and day-of-week.
  return sixOrLess.map((p) => p === '?' ? '*' : p).join(' ');
}

/**
 * Returns the next `count` fire times for a Quartz cron expression. Never throws: an invalid
 * expression yields an empty array and an error message.
 */
export function previewSchedule(cron: string, count = 5): {
  fireTimes: Date[];
  error: string | null;
} {
  if (!cron.trim()) return { fireTimes: [], error: i18n.t('editor:cron.empty') };
  try {
    const normalized = normalizeQuartzCron(cron);
    const it = CronExpressionParser.parse(normalized, { currentDate: new Date() });
    const fireTimes: Date[] = [];
    for (let i = 0; i < count; i++) {
      fireTimes.push(it.next().toDate());
    }
    return { fireTimes, error: null };
  } catch (e) {
    return { fireTimes: [], error: (e as Error).message };
  }
}

/** Relative time description such as "in 3m 22s" or "in 2 days". Approximate wording,
 *  intended for the preview only. */
export function relativeFromNow(date: Date): string {
  const diffMs = date.getTime() - Date.now();
  if (diffMs <= 0) return i18n.t('editor:cron.now');
  const s = Math.floor(diffMs / 1000);
  if (s < 60) return i18n.t('editor:cron.inSeconds', { s });
  const m = Math.floor(s / 60);
  if (m < 60) return i18n.t('editor:cron.inMinutes', { m, s: s % 60 });
  const h = Math.floor(m / 60);
  if (h < 24) return i18n.t('editor:cron.inHours', { h, m: m % 60 });
  const d = Math.floor(h / 24);
  return i18n.t('editor:cron.inDays', { d, h: h % 24 });
}
