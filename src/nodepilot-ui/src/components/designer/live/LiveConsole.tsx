import { Filter, Pause, Play, WarningAltFilled } from '@carbon/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { LiveExecution, StepUpdate } from '../../../hooks/useSignalR';

interface Props {
  execution: LiveExecution;
  /** Click a line → caller selects the originating step (typically opens the inspector). */
  onSelectStep?: (stepId: string) => void;
}

/**
 * Live "build-log" tail of every step's stdout/stderr/transcript output, aggregated
 * in execution-time order. Complements the Gantt (when?) and the Step-Inspector
 * (deep dive) by answering the broad "what's the run printing right now?" question
 * without forcing the user to click each step.
 *
 * Lines are derived from `execution.steps[].output | errorOutput | traceOutput` —
 * SignalR keeps these in sync, so the component just renders whatever's currently
 * on the steps and re-sorts when new step events arrive.
 *
 * Volume guard: only the last MAX_LINES are shown; older lines collapse into a
 * "(N earlier lines hidden)" header. RunScripts can dump megabytes; we don't want
 * the bottom panel to OOM the browser.
 */
const MAX_LINES = 1000;

interface Line {
  /** Stable key — combination of stepId + kind + line index inside that step's output. */
  key: string;
  stepId: string;
  stepName: string;
  stepType: string;
  /** Time relative to execution start, in ms. Lines from a step share the step's startedAt
   *  because step-level output isn't per-line timestamped on the wire. */
  offsetMs: number;
  kind: 'out' | 'err' | 'trace';
  text: string;
}

export function LiveConsole({ execution, onSelectStep }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const [filter, setFilter] = useState('');
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [autoScroll, setAutoScroll] = useState(true);
  const scrollRef = useRef<HTMLDivElement>(null);

  const execStartMs = new Date(execution.startedAt).getTime();
  const allLines = useMemo(() => deriveLines(execution.steps, execStartMs), [execution.steps, execStartMs]);

  const filtered = useMemo(() => {
    let out = allLines;
    if (errorsOnly) out = out.filter((l) => l.kind === 'err');
    if (filter.trim().length > 0) {
      const q = filter.toLowerCase();
      out = out.filter((l) =>
        l.text.toLowerCase().includes(q) ||
        l.stepName.toLowerCase().includes(q) ||
        l.stepType.toLowerCase().includes(q),
      );
    }
    return out;
  }, [allLines, filter, errorsOnly]);

  const truncated = filtered.length > MAX_LINES;
  const visible = truncated ? filtered.slice(filtered.length - MAX_LINES) : filtered;

  // Auto-scroll to bottom whenever lines change AND the user hasn't paused. We don't
  // try to detect "user scrolled up" because that's flaky in jsdom-tested code; the
  // explicit pause toggle keeps the contract obvious.
  useEffect(() => {
    if (!autoScroll || !scrollRef.current) return;
    scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [visible.length, autoScroll]);

  const errorCount = allLines.filter((l) => l.kind === 'err').length;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Toolbar */}
      <div className="flex items-center gap-2 px-2 py-1.5 border-b border-outline-variant/10 bg-surface-low/40 shrink-0">
        <div className="relative flex-1 max-w-md">
          <Filter size={11} className="absolute left-2 top-1/2 -translate-y-1/2 text-outline" />
          <input
            type="text"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={t('live.console.filterPlaceholder')}
            className="w-full pl-7 pr-2 py-1 text-[11px] font-mono bg-surface-high border border-outline-variant/20 rounded outline-none focus:border-primary/50 placeholder:text-outline"
          />
        </div>
        <button
          type="button"
          onClick={() => setErrorsOnly((x) => !x)}
          className={`flex items-center gap-1 px-2 py-1 rounded text-[10px] font-label font-semibold transition-colors ${
            errorsOnly ? 'bg-error/10 text-error' : 'text-on-surface-variant hover:bg-surface-high'
          }`}
          title={t('live.console.showOnlyErrors')}
        >
          <WarningAltFilled size={10} />
          {t('live.console.errors')}
          {errorCount > 0 && (
            <span className={`ml-0.5 px-1 rounded-full text-[9px] ${errorsOnly ? 'bg-error/20' : 'bg-surface-highest'}`}>
              {errorCount}
            </span>
          )}
        </button>
        <button
          type="button"
          onClick={() => setAutoScroll((x) => !x)}
          className={`flex items-center gap-1 px-2 py-1 rounded text-[10px] font-label font-semibold transition-colors ${
            autoScroll ? 'text-primary hover:bg-surface-high' : 'bg-surface-highest text-on-surface'
          }`}
          title={autoScroll ? t('live.console.pauseAutoScroll') : t('live.console.resumeAutoScroll')}
        >
          {autoScroll ? <Pause size={10} /> : <Play size={10} />}
          {autoScroll ? t('live.console.live') : t('live.console.paused')}
        </button>
      </div>
      {/* Lines */}
      <div
        ref={scrollRef}
        className="flex-1 overflow-y-auto bg-surface-low font-mono text-[10.5px] leading-relaxed"
        data-testid="live-console-stream"
      >
        {visible.length === 0 ? (
          <div className="flex items-center justify-center h-full text-outline font-label text-xs">
            {allLines.length === 0
              ? t('live.console.waitingForOutput')
              : t('live.console.noLinesMatch', { filter, errorsSuffix: errorsOnly ? t('live.console.errorsOnlySuffix') : '' })}
          </div>
        ) : (
          <>
            {truncated && (
              <div className="px-3 py-1 bg-warning-container/60 border-b border-warning/30 text-[10px] font-label text-on-warning-container">
                {t('live.console.earlierLinesHidden', { count: filtered.length - MAX_LINES, cap: MAX_LINES.toLocaleString() })}
              </div>
            )}
            {visible.map((l) => (
              <ConsoleLine key={l.key} line={l} onSelect={onSelectStep} />
            ))}
          </>
        )}
      </div>
    </div>
  );
}

function ConsoleLine({ line, onSelect }: Readonly<{ line: Line; onSelect?: (id: string) => void }>) {
  const { t } = useTranslation('designer');
  const kindStyle =
    line.kind === 'err' ? 'text-red-600' :
    line.kind === 'trace' ? 'text-on-surface-variant italic' :
    'text-on-surface';
  return (
    <div
      onClick={onSelect ? () => onSelect(line.stepId) : undefined}
      onKeyDown={onSelect ? (e) => (e.key === 'Enter' || e.key === ' ') && onSelect(line.stepId) : undefined}
      role={onSelect ? 'button' : undefined}
      tabIndex={onSelect ? 0 : undefined}
      className={`group flex items-baseline gap-2 px-3 py-px hover:bg-surface-low/60 ${onSelect ? 'cursor-pointer' : ''} ${kindStyle}`}
      title={onSelect ? t('live.console.inspectStep') : undefined}
    >
      <span className="text-outline tabular-nums shrink-0 w-12 text-right">+{formatMs(line.offsetMs)}</span>
      <span className="text-on-surface-variant/60 shrink-0 w-32 truncate" title={`${line.stepName} · ${line.stepType}`}>
        {line.stepName}
      </span>
      <span className="whitespace-pre-wrap break-words flex-1 min-w-0">{line.text}</span>
    </div>
  );
}

function deriveLines(steps: StepUpdate[], execStartMs: number): Line[] {
  const sorted = [...steps].sort((a, b) => {
    if (!a.startedAt) return 1;
    if (!b.startedAt) return -1;
    return new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime();
  });

  const lines: Line[] = [];
  for (const s of sorted) {
    const offsetMs = s.startedAt ? new Date(s.startedAt).getTime() - execStartMs : 0;
    const stepName = s.stepName || s.stepId;
    pushSplit(lines, s.output, 'out', s, offsetMs, stepName);
    pushSplit(lines, s.errorOutput, 'err', s, offsetMs, stepName);
    pushSplit(lines, s.traceOutput, 'trace', s, offsetMs, stepName);
  }
  return lines;
}

function pushSplit(
  acc: Line[],
  raw: string | null | undefined,
  kind: Line['kind'],
  s: StepUpdate,
  offsetMs: number,
  stepName: string,
) {
  if (!raw) return;
  // Split on \r\n or \n, drop trailing empty line so a single-line output doesn't yield 2 entries.
  const parts = raw.split(/\r?\n/);
  while (parts.length > 0 && parts.at(-1) === '') parts.pop();
  parts.forEach((text, i) => {
    acc.push({
      key: `${s.stepId}::${kind}::${i}`,
      stepId: s.stepId,
      stepName,
      stepType: s.stepType,
      offsetMs,
      kind,
      text,
    });
  });
}

function formatMs(ms: number): string {
  if (ms < 0) ms = 0;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m${s}s`;
}
