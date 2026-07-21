import { CronExpressionParser } from 'cron-parser';
import i18n from '../i18n';

/**
 * Normalizes a Quartz cron expression (7 fields) into a 6-field expression the cron-parser
 * library can consume. Quartz allows `?` as an "unspecified" placeholder on either
 * day-of-month OR day-of-week (whichever field isn't set); cron-parser requires every field
 * to hold an actual range, so we swap `?` for `*`. The seventh field (year) is dropped
 * entirely — it's almost never used, and cron-parser already projects future fire times
 * indefinitely without needing a year bound.
 */
export function normalizeQuartzCron(cron: string): string {
  const trimmed = cron.trim();
  if (!trimmed) return trimmed;
  const parts = trimmed.split(/\s+/);
  // At most 7 fields (seconds+minutes+hours+dom+month+dow+year); drop the year field.
  const sixOrLess = parts.slice(0, 6);
  // ? → * for day-of-month and day-of-week
  return sixOrLess.map((p) => p === '?' ? '*' : p).join(' ');
}

/**
 * Returns the next <paramref name="count"/> fire times for a Quartz cron expression.
 * Never throws — an invalid cron expression yields an empty array plus an error message.
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

/** Relative time description ("in 3m 22s", "in 2 days"). Not locale-perfect, but good
 *  enough for this preview feature. */
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
