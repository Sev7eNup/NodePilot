import i18n from '../i18n';

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
