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
 * Formats a future timestamp relative to "now". Used by the dashboard's armed-trigger
 * panel to render "in 5m" / "in 2h 15m" / "tomorrow 03:00" / absolute date for >24h.
 *
 * `now` is exposed so a parent component can drive re-rendering on a minute-tick
 * without each call recomputing its own clock — see useMinuteTick().
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
  // ≥24h away → absolute weekday + time, e.g. "Wed 14:00".
  return new Date(iso).toLocaleString(currentLocale(), {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function formatDate(iso: string, opts?: Intl.DateTimeFormatOptions): string {
  return new Date(iso).toLocaleString(currentLocale(), opts);
}

export function formatDateOnly(iso: string, opts?: Intl.DateTimeFormatOptions): string {
  return new Date(iso).toLocaleDateString(currentLocale(), opts);
}

export function formatNumber(value: number, opts?: Intl.NumberFormatOptions): string {
  return value.toLocaleString(currentLocale(), opts);
}
