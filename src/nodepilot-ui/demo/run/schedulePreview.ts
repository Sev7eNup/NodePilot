import { CronExpressionParser } from 'cron-parser';

/** Offline demo subset only. Production uses the server's Quartz endpoint. */
export function normalizeQuartzCron(cron: string): string {
  const parts = cron.trim().split(/\s+/);
  if (parts.length < 6 || parts.length > 7)
    throw new Error('Quartz schedules require six or seven fields.');
  if (parts.length === 7 && parts[6] !== '*')
    throw new Error('Year-restricted schedule previews require a NodePilot server.');
  const weekday = parts[5].toUpperCase();
  if (!['*', '?'].includes(weekday)
    && !/^(?:[1-7]|SUN|MON|TUE|WED|THU|FRI|SAT)(?:[-,](?:[1-7]|SUN|MON|TUE|WED|THU|FRI|SAT))*$/.test(weekday))
    throw new Error('This weekday expression requires a NodePilot server for its preview.');
  parts[5] = weekday.replace(/[1-7]/g, number => String(Number(number) - 1));
  return parts.slice(0, 6).map(part => part === '?' ? '*' : part).join(' ');
}

export function previewSchedule(cron: string, count = 5): { fireTimes: Date[]; error: string | null } {
  if (!cron.trim()) return { fireTimes: [], error: 'Cron expression is empty.' };
  try {
    const iterator = CronExpressionParser.parse(normalizeQuartzCron(cron), { currentDate: new Date() });
    return { fireTimes: Array.from({ length: Math.max(1, Math.min(count, 20)) }, () => iterator.next().toDate()), error: null };
  } catch (error) {
    return { fireTimes: [], error: (error as Error).message };
  }
}
