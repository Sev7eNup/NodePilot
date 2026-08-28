import i18n from '../i18n';

function currentLocale(): string {
  const lng = i18n.language || 'de';
  return lng === 'en' ? 'en-US' : 'de-DE';
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null || !isFinite(ms)) return i18n.t('format:noValue');
  if (ms < 1000) return i18n.t('format:ms', { value: Math.round(ms) });
  const s = ms / 1000;
  if (s < 60) return i18n.t('format:seconds', { value: s.toFixed(s < 10 ? 1 : 0) });
  const m = Math.floor(s / 60);
  const rs = Math.round(s % 60);
  return i18n.t('format:minutes', { m, s: rs });
}

export function formatRelative(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) return i18n.t('format:justNow');
  if (diff < 3_600_000) return i18n.t('format:minutesAgo', { count: Math.floor(diff / 60_000) });
  if (diff < 86_400_000) return i18n.t('format:hoursAgo', { count: Math.floor(diff / 3_600_000) });
  if (diff < 7 * 86_400_000) return i18n.t('format:daysAgo', { count: Math.floor(diff / 86_400_000) });
  return new Date(iso).toLocaleDateString(currentLocale());
}

/**
 * Formats a future timestamp relative to now, as "in 5m", "in 2h 15m", or an absolute
 * weekday and time beyond 24 hours.
 *
 * `now` is a parameter so a parent component can drive re-rendering on a minute tick
 * without each call recomputing its own clock; see useMinuteTick().
 */
export function formatRelativeFuture(iso: string, now: number = Date.now()): string {
  const target = new Date(iso).getTime();
  const diff = target - now;
  if (diff <= 0) return i18n.t('format:dueNow');
  if (diff < 60_000) return i18n.t('format:inSeconds', { count: Math.max(1, Math.floor(diff / 1000)) });
  if (diff < 3_600_000) return i18n.t('format:inMinutes', { count: Math.floor(diff / 60_000) });
  if (diff < 86_400_000) {
    const hours = Math.floor(diff / 3_600_000);
    const minutes = Math.floor((diff % 3_600_000) / 60_000);
    return minutes > 0
      ? i18n.t('format:inHoursMinutes', { hours, minutes })
      : i18n.t('format:inHours', { count: hours });
  }
  // 24h or more away: absolute weekday and time, for example "Wed 14:00".
  return new Date(iso).toLocaleString(currentLocale(), {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/**
 * Accepted input for the date/time helpers: ISO string, epoch milliseconds, or a Date.
 * Call sites deal in all three shapes, so the helpers normalize instead of forcing callers
 * to wrap values in `new Date(...)`.
 */
export type DateInput = string | number | Date;

export function formatDate(value: DateInput, opts?: Intl.DateTimeFormatOptions): string {
  return new Date(value).toLocaleString(currentLocale(), opts);
}

export function formatDateOnly(value: DateInput, opts?: Intl.DateTimeFormatOptions): string {
  return new Date(value).toLocaleDateString(currentLocale(), opts);
}

/**
 * Time-only variant for clock labels on timelines and step timestamps. Components must use
 * this instead of calling toLocaleTimeString directly (lint-enforced): a bare or `[]`
 * locale argument falls back to the browser locale and ignores the UI language.
 */
export function formatTime(value: DateInput, opts?: Intl.DateTimeFormatOptions): string {
  return new Date(value).toLocaleTimeString(currentLocale(), opts);
}

export function formatNumber(value: number, opts?: Intl.NumberFormatOptions): string {
  return value.toLocaleString(currentLocale(), opts);
}
