import type { AppLogEntry, AppLogLevel } from '../api/diagnostics';

/** Functional area of a log source, shown instead of the .NET class name. */
export type AppLogArea =
  | 'engine' | 'database' | 'scheduler' | 'remote' | 'ai' | 'auth' | 'web' | 'other';

export const APP_LOG_AREAS: AppLogArea[] =
  ['engine', 'database', 'scheduler', 'remote', 'ai', 'auth', 'web', 'other'];

// First match wins, so the auth patterns come before the generic NodePilot.Api prefix.
const AREA_RULES: [RegExp, AppLogArea][] = [
  [/(^|\.)(Security|Auth|Authentication|Ldap|Scim|Oidc)(\.|$)/i, 'auth'],
  [/^NodePilot\.Engine/, 'engine'],
  [/^(NodePilot\.Data|Npgsql|Microsoft\.EntityFrameworkCore|Microsoft\.Data)/, 'database'],
  [/^NodePilot\.Scheduler/, 'scheduler'],
  [/^NodePilot\.Remote/, 'remote'],
  [/^NodePilot\.Ai/, 'ai'],
  [/^(Microsoft\.AspNetCore|Microsoft\.Hosting|Microsoft\.Extensions\.Diagnostics\.HealthChecks)/, 'web'],
];

export function areaOf(source: string): AppLogArea {
  return AREA_RULES.find(([pattern]) => pattern.test(source))?.[1] ?? 'other';
}

export function isProblem(level: AppLogLevel): boolean {
  return level === 'Warning' || level === 'Error' || level === 'Fatal';
}

export function isError(level: AppLogLevel): boolean {
  return level === 'Error' || level === 'Fatal';
}

// Only values that differ per occurrence are replaced. Short numbers and codes carry meaning
// (HTTP 401 vs 500, 0x80070005, SQLSTATE 28P01, ports) and stay, so different causes never merge.
const VOLATILE: [RegExp, string][] = [
  [/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '<id>'],
  [/\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?/g, '<time>'],
  [/\b\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\b/g, '<time>'],
  [/\b\d+(?:[.,]\d+)?\s?(?:ms|milliseconds|sec|seconds|s)\b/gi, '<duration>'],
  [/\b[0-9a-f]{12,}\b/gi, '<hex>'],
  [/\b\d{5,}\b/g, '<n>'],
];

export function normalizeMessage(message: string): string {
  return VOLATILE.reduce((text, [pattern, token]) => text.replace(pattern, token), message);
}

/** Entries that share source, level and cause. Entries with a message template group by the template. */
export function groupKey(entry: AppLogEntry): string {
  return `${entry.level}|${entry.source}|${entry.template ?? normalizeMessage(entry.message)}`;
}

export type AppLogGroup = {
  key: string;
  level: AppLogLevel;
  source: string;
  area: AppLogArea;
  /** Occurrences among the entries that were passed in, not in the whole log. */
  count: number;
  firstSeen: string;
  lastSeen: string;
  /** The newest occurrence, shown when the card is expanded. */
  latest: AppLogEntry;
};

/** Groups warnings and errors. Errors first, then by most recent occurrence. */
export function groupProblems(entries: AppLogEntry[]): AppLogGroup[] {
  const groups = new Map<string, AppLogGroup>();
  for (const entry of entries) {
    if (!isProblem(entry.level)) continue;
    const key = groupKey(entry);
    const existing = groups.get(key);
    if (!existing) {
      groups.set(key, {
        key, level: entry.level, source: entry.source, area: areaOf(entry.source),
        count: 1, firstSeen: entry.timestamp, lastSeen: entry.timestamp, latest: entry,
      });
      continue;
    }
    existing.count += 1;
    if (Date.parse(entry.timestamp) > Date.parse(existing.lastSeen)) {
      existing.lastSeen = entry.timestamp;
      existing.latest = entry;
    }
    if (Date.parse(entry.timestamp) < Date.parse(existing.firstSeen)) existing.firstSeen = entry.timestamp;
  }
  return [...groups.values()].sort((a, b) =>
    Number(isError(b.level)) - Number(isError(a.level))
    || Date.parse(b.lastSeen) - Date.parse(a.lastSeen));
}
