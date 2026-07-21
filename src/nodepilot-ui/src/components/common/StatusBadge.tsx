import {
  CheckmarkFilled,
  CircleDash,
  ErrorFilled,
  Information,
  Pause,
  SubtractAlt,
  Time,
  Timer,
  WarningAltFilled,
  type CarbonIconType,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import {
  npStatusFromExecution, STATUS_BADGE_CLASS, isKnownRawStatus, rawStatusLabelKey,
} from '../../lib/statusTokens';

/**
 * Shared status pill, backed by `statusTokens` + the `executions:status.*` i18n namespace.
 *
 * Contract: takes a RAW backend status string (PascalCase: "Succeeded", "Failed",
 * "TimedOut", "Running", …). Known statuses are styled via `STATUS_BADGE_CLASS` and labeled
 * via `executions:status.*` (keyed by the raw string so `TimedOut`/`Queued` keep distinct
 * labels — `NpStatus` merges them and is only used for styling). UNKNOWN statuses render
 * their raw string as-is with neutral styling — this deliberately does NOT fall back to
 * `info`, so a newly introduced backend status is surfaced verbatim instead of masked.
 */
const ICON_BY_RAW_KEY: Record<string, CarbonIconType> = {
  succeeded: CheckmarkFilled,
  success: CheckmarkFilled,
  failed: ErrorFilled,
  timedout: Timer,
  running: CircleDash,
  pending: Time,
  queued: Time,
  cancelled: SubtractAlt,
  canceled: SubtractAlt,
  paused: Pause,
  skipped: SubtractAlt,
  warning: WarningAltFilled,
  info: Information,
};

const NEUTRAL_CLASS = 'bg-surface-high text-on-surface-variant';

export function StatusBadge({
  status, size = 'md', icon: IconOverride,
}: Readonly<{ status: string; size?: 'sm' | 'md'; icon?: CarbonIconType }>) {
  const { t } = useTranslation(['executions']);
  const known = isKnownRawStatus(status);
  const labelKey = rawStatusLabelKey(status);
  const np = npStatusFromExecution(status);

  const Icon = IconOverride ?? (known && labelKey ? ICON_BY_RAW_KEY[labelKey] : Information);
  const label = known && labelKey ? t(`executions:status.${labelKey}`) : status;
  const cls = known ? STATUS_BADGE_CLASS[np] : NEUTRAL_CLASS;
  const spin = known && labelKey === 'running';
  const sizeCls = size === 'sm' ? 'text-[10px] px-1.5 py-0.5' : 'text-xs px-2 py-0.5';

  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-full font-medium whitespace-nowrap ${sizeCls} ${cls}`}>
      <Icon size={size === 'sm' ? 10 : 11} className={`shrink-0 ${spin ? 'animate-spin' : ''}`} aria-hidden />
      {label}
    </span>
  );
}