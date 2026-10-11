import { CheckmarkFilled, Download, ErrorFilled, Pause, Play, Terminal, WarningFilled } from '@carbon/icons-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { diagnostics, type AppLogEntry, type AppLogLevel, type AppLogQuery, type AppLogResponse } from '../../api/diagnostics';
import { APP_LOG_AREAS, areaOf, groupProblems, isError, isProblem, type AppLogArea } from '../../lib/appLog';
import { formatDate } from '../../lib/format';
import { toast } from '../../stores/toastStore';

type View = 'problems' | 'history';
type MinLevel = NonNullable<AppLogQuery['minLevel']>;

const WINDOW_HOURS = [1, 24, 168] as const;
const POLL_MS = 10_000;

/** Today in the browser's local calendar; the server names its daily files by local date too. */
function todayLocal(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

function levelTone(level: AppLogLevel): string {
  if (isError(level)) return 'text-red-600 dark:text-red-400';
  if (level === 'Warning') return 'text-amber-600 dark:text-amber-400';
  return 'text-on-surface-variant';
}

/**
 * Admin-only view of the application log (the server's own log, not the workflow events).
 * "Problems" folds repeated warnings and errors into one card per cause; "History" lists every entry.
 * Every number on screen is tied to the part of the log that was actually read.
 */
export function AppLogViewerSection() {
  const { t } = useTranslation('supportLog');
  const [view, setView] = useState<View>('problems');
  const [hours, setHours] = useState<number>(24);
  const [paused, setPaused] = useState(false);
  const [minLevel, setMinLevel] = useState<MinLevel>('Information');
  const [area, setArea] = useState<AppLogArea | 'all'>('all');
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [downloadDate, setDownloadDate] = useState(todayLocal);

  const request: AppLogQuery = view === 'problems'
    ? { hours, minLevel: 'Warning', take: 1000 }
    : { hours, minLevel, q: search || undefined, take: 500 };
  const { data, isLoading, error } = useQuery({
    queryKey: ['diagnostics', 'app-log', request],
    queryFn: () => diagnostics.tailAppLog(request),
    placeholderData: keepPreviousData,
    refetchInterval: paused ? false : POLL_MS,
    refetchOnWindowFocus: true,
  });

  const tab = (id: View, label: string) => (
    <button type="button" onClick={() => setView(id)} aria-pressed={view === id}
      className={`px-3 py-1.5 ${id === 'history' ? 'border-l border-outline-variant' : ''} ${
        view === id ? 'bg-blue-600 text-white' : 'hover:bg-surface-low'}`}>
      {label}
    </button>
  );

  return (
    <div className="np-card p-4 space-y-3">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h3 className="font-semibold text-on-surface flex items-center gap-2">
          <Terminal size={18} /> {t('appLog.title')}
        </h3>
        <div className="inline-flex rounded border border-outline-variant overflow-hidden text-xs">
          {tab('problems', t('appLog.viewProblems'))}
          {tab('history', t('appLog.viewHistory'))}
        </div>
      </div>
      <div className="flex items-center justify-end gap-2 flex-wrap">
        <div className="flex items-center gap-2 text-xs">
          <select value={hours} onChange={(e) => setHours(Number.parseInt(e.target.value, 10))}
            aria-label={t('appLog.window')}
            className="px-2 py-1 border border-outline-variant rounded text-xs">
            {WINDOW_HOURS.map((h) => <option key={h} value={h}>{t(`appLog.windowOption.${h}`)}</option>)}
          </select>
          <button type="button" onClick={() => setPaused(!paused)}
            className="flex items-center gap-1 px-2 py-1 border border-outline-variant rounded hover:bg-surface-low">
            {paused ? <Play size={12} /> : <Pause size={12} />} {paused ? t('appLog.resume') : t('appLog.pause')}
          </button>
        </div>
      </div>

      {error && <p role="alert" className="text-sm text-red-700 dark:text-red-400">{(error as Error).message}</p>}
      {isLoading && <p className="text-sm text-on-surface-variant">{t('loading')}</p>}
      {data && <StatusPanel data={data} />}

      {data && view === 'problems' && <ProblemsView data={data} />}
      {data && view === 'history' && (
        <HistoryView data={data} minLevel={minLevel} onMinLevel={setMinLevel}
          area={area} onArea={setArea} draft={draft} onDraft={setDraft}
          onSearch={() => setSearch(draft.trim())} />
      )}

      <div className="flex items-center gap-2 text-sm pt-2 border-t border-outline-variant flex-wrap">
        <label htmlFor="app-log-export-date" className="text-xs font-medium text-on-surface-variant">
          {t('appLog.exportLabel')}
        </label>
        <input id="app-log-export-date" type="date" value={downloadDate}
          onChange={(e) => setDownloadDate(e.target.value)}
          className="px-2 py-1 border border-outline-variant rounded text-xs" />
        <button type="button"
          onClick={() => {
            diagnostics.downloadAppLog(downloadDate).catch((err: Error) => toast.error(err.message));
          }}
          className="flex items-center gap-1 px-3 py-1.5 text-xs bg-blue-600 text-white hover:bg-blue-700 rounded">
          <Download size={12} /> {t('appLog.export')}
        </button>
        <span className="text-xs text-on-surface-variant">{t('appLog.exportHint')}</span>
      </div>
    </div>
  );
}

/** Totals plus an honest statement of how much of the window the server read. */
function StatusPanel({ data }: Readonly<{ data: AppLogResponse }>) {
  const { t } = useTranslation('supportLog');
  const { scan, summary } = data;
  const since = formatDate(scan.complete ? scan.requestedSince : (scan.coverageStart ?? scan.requestedSince));
  const clean = summary.errors === 0 && summary.warnings === 0;

  let headline: string;
  if (clean) {
    headline = t(scan.complete ? 'appLog.statusClean' : 'appLog.statusCleanPartial', { since });
  } else {
    const counts = [
      t('appLog.errorsCount', { count: summary.errors }),
      t('appLog.warningsCount', { count: summary.warnings }),
    ].join(' · ');
    headline = t(scan.complete ? 'appLog.statusProblems' : 'appLog.statusProblemsPartial', { since, counts });
  }

  const notes: string[] = [];
  if (!scan.complete) notes.push(t('appLog.partialNote'));
  if (scan.budgetExhausted && !scan.complete) {
    notes.push(t('appLog.budgetNote', { mb: Math.round(scan.bytesScanned / (1024 * 1024)) }));
  }
  if (scan.filesSkipped > 0) notes.push(t('appLog.skippedNote', { count: scan.filesSkipped }));
  if (scan.unparsedLines > 0) notes.push(t('appLog.unparsedNote', { count: scan.unparsedLines }));
  if (data.results.truncated) {
    notes.push(t('appLog.resultsNote', { returned: data.results.returned, matched: data.results.matched }));
  }

  const hasErrors = summary.errors > 0;
  const Icon = clean && scan.complete ? CheckmarkFilled : hasErrors ? ErrorFilled : WarningFilled;
  const tone = clean && scan.complete
    ? 'text-green-600 dark:text-green-400'
    : hasErrors ? 'text-red-600 dark:text-red-400' : 'text-amber-600 dark:text-amber-400';

  return (
    <div className="rounded border border-outline-variant p-3 space-y-1" role="status">
      <p className={`flex items-center gap-2 text-sm font-semibold ${tone}`}>
        <Icon size={16} /> {headline}
      </p>
      {notes.map((note) => <p key={note} className="text-xs text-on-surface-variant">{note}</p>)}
    </div>
  );
}

function ProblemsView({ data }: Readonly<{ data: AppLogResponse }>) {
  const { t } = useTranslation('supportLog');
  const groups = groupProblems(data.entries);
  if (groups.length === 0) {
    return <p className="text-sm text-on-surface-variant">{t('appLog.noProblems')}</p>;
  }
  return (
    <div className="space-y-2">
      {data.results.truncated && (
        <p className="text-xs text-on-surface-variant">{t('appLog.groupedNote', { returned: data.results.returned })}</p>
      )}
      {groups.map((g) => (
        <details key={g.key} className="rounded border border-outline-variant group">
          <summary className="flex items-start gap-3 p-3 cursor-pointer list-none">
            <span className={`shrink-0 text-xs font-semibold w-16 ${levelTone(g.level)}`}>
              {t(`appLog.levels.${g.level}`)}
            </span>
            <span className="flex-1 min-w-0">
              <span className="block text-sm text-on-surface break-words">{g.latest.message}</span>
              <span className="block text-xs text-on-surface-variant mt-0.5">
                {t(`appLog.areas.${g.area}`)} · {t('appLog.lastSeen', { time: formatDate(g.lastSeen) })}
              </span>
            </span>
            <span className="shrink-0 text-xs font-mono px-2 py-0.5 rounded bg-surface-low"
              title={t('appLog.countHint')}>
              {g.count}×
            </span>
          </summary>
          <EntryDetails entry={g.latest} extra={t('appLog.firstSeen', { time: formatDate(g.firstSeen) })} />
        </details>
      ))}
    </div>
  );
}

type HistoryProps = Readonly<{
  data: AppLogResponse;
  minLevel: MinLevel;
  onMinLevel: (level: MinLevel) => void;
  area: AppLogArea | 'all';
  onArea: (area: AppLogArea | 'all') => void;
  draft: string;
  onDraft: (value: string) => void;
  onSearch: () => void;
}>;

function HistoryView({ data, minLevel, onMinLevel, area, onArea, draft, onDraft, onSearch }: HistoryProps) {
  const { t } = useTranslation('supportLog');
  const entries = area === 'all' ? data.entries : data.entries.filter((e) => areaOf(e.source) === area);
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2 flex-wrap text-xs">
        <select value={minLevel} onChange={(e) => onMinLevel(e.target.value as MinLevel)}
          aria-label={t('appLog.severity')}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          <option value="Information">{t('appLog.severityAll')}</option>
          <option value="Warning">{t('appLog.severityWarning')}</option>
          <option value="Error">{t('appLog.severityError')}</option>
        </select>
        <select value={area} onChange={(e) => onArea(e.target.value as AppLogArea | 'all')}
          aria-label={t('appLog.area')}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          <option value="all">{t('appLog.areaAll')}</option>
          {APP_LOG_AREAS.map((a) => <option key={a} value={a}>{t(`appLog.areas.${a}`)}</option>)}
        </select>
        <form className="flex items-center gap-1 ml-auto" onSubmit={(e) => { e.preventDefault(); onSearch(); }}>
          <input type="search" value={draft} onChange={(e) => onDraft(e.target.value)}
            aria-label={t('appLog.search')} placeholder={t('appLog.search')}
            className="px-2 py-1 border border-outline-variant rounded text-xs w-56" />
          <button type="submit" className="px-2 py-1 border border-outline-variant rounded hover:bg-surface-low">
            {t('appLog.searchButton')}
          </button>
        </form>
      </div>
      {entries.length === 0 ? (
        <p className="text-sm text-on-surface-variant">{t('appLog.noEntries')}</p>
      ) : (
        <ul className="divide-y divide-outline-variant border border-outline-variant rounded">
          {entries.map((entry, i) => (
            <li key={`${entry.timestamp}-${i}`}>
              <details>
                <summary className="flex items-baseline gap-3 px-3 py-1.5 cursor-pointer list-none text-xs">
                  <span className="shrink-0 w-40 font-mono text-on-surface-variant">{formatDate(entry.timestamp)}</span>
                  <span className={`shrink-0 w-16 font-semibold ${levelTone(entry.level)}`}>
                    {t(`appLog.levels.${entry.level}`)}
                  </span>
                  <span className="shrink-0 w-32 text-on-surface-variant truncate" title={entry.source}>
                    {t(`appLog.areas.${areaOf(entry.source)}`)}
                  </span>
                  <span className={`flex-1 min-w-0 break-words ${isProblem(entry.level) ? 'text-on-surface' : 'text-on-surface-variant'}`}>
                    {entry.message}
                    {entry.truncated && <em className="ml-2 not-italic text-amber-600 dark:text-amber-400">{t('appLog.truncatedChip')}</em>}
                  </span>
                </summary>
                <EntryDetails entry={entry} />
              </details>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function EntryDetails({ entry, extra }: Readonly<{ entry: AppLogEntry; extra?: string }>) {
  const { t } = useTranslation('supportLog');
  return (
    <div className="px-3 pb-3 space-y-1 text-xs">
      <p className="text-on-surface-variant">
        {t('appLog.source')} <code>{entry.source}</code>{extra ? ` · ${extra}` : ''}
      </p>
      {entry.truncated && <p className="text-amber-600 dark:text-amber-400">{t('appLog.truncatedHint')}</p>}
      {entry.details ? (
        <pre className="bg-surface-lowest border border-outline-variant rounded p-2 font-mono text-[11px] leading-tight overflow-auto whitespace-pre-wrap break-all max-h-64">
          {entry.details}
        </pre>
      ) : (
        <p className="text-on-surface-variant">{t('appLog.noDetails')}</p>
      )}
    </div>
  );
}
