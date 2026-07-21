/**
 * Semantic status → design-token mapping. Single source of truth for status
 * coloring across the designer (edges, node rings, MiniMap, Gantt, badges,
 * banners). All values reference the status tokens declared in index.css
 * (`--color-success`, `--color-warning`, …) — never raw Tailwind palette
 * classes or hex literals. "failed" uses the existing M3 `--color-error`
 * family so the app keeps a single red.
 */
export type NpStatus =
  | 'success'
  | 'failed'
  | 'running'
  | 'paused'
  | 'skipped'
  | 'pending'
  | 'cancelled'
  | 'warning'
  | 'info';

/**
 * Maps a backend execution/step status string (PascalCase enum values like
 * "Succeeded", "Failed", "Running", …) to its semantic status. Unknown or
 * missing values fall back to 'info' (neutral, non-alarming).
 */
export function npStatusFromExecution(status: string | null | undefined): NpStatus {
  switch ((status ?? '').toLowerCase()) {
    case 'succeeded':
    case 'success':
      return 'success';
    case 'failed':
    case 'timedout':
      return 'failed';
    case 'running':
      return 'running';
    case 'paused':
      return 'paused';
    case 'skipped':
      return 'skipped';
    case 'pending':
    case 'queued':
      return 'pending';
    case 'cancelled':
    case 'canceled':
      return 'cancelled';
    case 'warning':
      return 'warning';
    default:
      return 'info';
  }
}

/** CSS color value per status for SVG/canvas consumers (edge strokes, MiniMap, Gantt bars). */
export const STATUS_COLOR_VAR: Record<NpStatus, string> = {
  success: 'var(--color-success)',
  failed: 'var(--color-error)',
  running: 'var(--color-running)',
  paused: 'var(--color-paused)',
  skipped: 'var(--color-skipped)',
  pending: 'var(--color-outline)',
  cancelled: 'var(--color-skipped)',
  warning: 'var(--color-warning)',
  info: 'var(--color-info)',
};

/** Tonal badge/chip classes (container bg + on-container text) per status. */
export const STATUS_BADGE_CLASS: Record<NpStatus, string> = {
  success: 'bg-success-container text-on-success-container',
  failed: 'bg-error-container text-on-error-container',
  running: 'bg-running-container text-on-running-container',
  paused: 'bg-paused-container text-on-paused-container',
  skipped: 'bg-skipped-container text-on-skipped-container',
  pending: 'bg-surface-high text-on-surface-variant',
  cancelled: 'bg-skipped-container text-on-skipped-container',
  warning: 'bg-warning-container text-on-warning-container',
  info: 'bg-info-container text-on-info-container',
};

/** Plain text color class per status (inline labels, counters). */
export const STATUS_TEXT_CLASS: Record<NpStatus, string> = {
  success: 'text-success',
  failed: 'text-error',
  running: 'text-running',
  paused: 'text-paused',
  skipped: 'text-skipped',
  pending: 'text-outline',
  cancelled: 'text-skipped',
  warning: 'text-warning',
  info: 'text-info',
};

/** Solid dot/indicator background class per status (status dots, progress segments). */
export const STATUS_DOT_CLASS: Record<NpStatus, string> = {
  success: 'bg-success',
  failed: 'bg-error',
  running: 'bg-running',
  paused: 'bg-paused',
  skipped: 'bg-skipped',
  pending: 'bg-outline',
  cancelled: 'bg-skipped',
  warning: 'bg-warning',
  info: 'bg-info',
};

/**
 * Known raw backend status strings (PascalCase). `NpStatus` collapses `TimedOut`→`failed`
 * and `Queued`→`pending`, so it CANNOT carry distinct labels for those. For label lookup,
 * use `rawStatusLabelKey()` (keyed by the raw string) — `NpStatus` is only for styling.
 * Comparison is case-insensitive (backend emits PascalCase, but be robust).
 */
const KNOWN_RAW_STATUSES = new Set([
  'succeeded', 'success', 'failed', 'timedout', 'running', 'pending', 'queued',
  'cancelled', 'canceled', 'paused', 'skipped', 'warning', 'info',
]);

/** Whether a raw status string is a recognized backend status (case-insensitive). */
export function isKnownRawStatus(status: string | null | undefined): boolean {
  return status != null && KNOWN_RAW_STATUSES.has(status.toLowerCase());
}

/**
 * i18n key suffix (under `executions:status.*`) for a known raw backend status, or `null`
 * for unknown statuses (caller renders the raw string as-is). Keyed by the raw string so
 * `TimedOut` and `Queued` get their own labels even though `NpStatus` merges them.
 */
export function rawStatusLabelKey(status: string | null | undefined): string | null {
  if (status == null) return null;
  const key = status.toLowerCase();
  if (!KNOWN_RAW_STATUSES.has(key)) return null;
  // Normalize the few aliases to their canonical label key.
  if (key === 'success') return 'succeeded';
  if (key === 'canceled') return 'cancelled';
  return key;
}
