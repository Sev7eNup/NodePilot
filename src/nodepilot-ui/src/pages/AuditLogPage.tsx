import { ChevronDown, ChevronRight, Download, Renew, Search } from '@carbon/icons-react';
import { useState, useMemo, useCallback, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { formatDate } from '../lib/format';
import { useIsMobile } from '../hooks/useMediaQuery';

interface AuditEntry {
  id: string;
  timestamp: string;
  userId: string | null;
  username: string | null;
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  details: string | null;
  ipAddress: string | null;
}

interface AuditCursor {
  timestamp: string;
  id: string;
}

interface AuditPageResponse {
  items: AuditEntry[];
  nextCursor: AuditCursor | null;
}

/**
 * Admin-only Audit-Log-Viewer. Reads the cursor-paginated `GET /api/audit` endpoint with
 * the current filter set, renders the response newest-first, and lets the operator load
 * more pages via the `nextCursor` token. The streaming export endpoint runs against the
 * same filter set so what you see on screen is what you get in the CSV/NDJSON.
 */
export function AuditLogPage() {
  const { t } = useTranslation(['audit', 'common']);
  const isMobile = useIsMobile();
  const [action, setAction] = useState('');
  const [resourceType, setResourceType] = useState('');
  const [resourceId, setResourceId] = useState('');
  const [userId, setUserId] = useState('');
  const [ipAddress, setIpAddress] = useState('');
  const [since, setSince] = useState('');
  const [until, setUntil] = useState('');
  const [take, setTake] = useState(100);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  // Accumulated pages: each "Load more" appends the next page to this list. Resetting the
  // filters resets it back to the first page automatically via queryKey invalidation.
  const [extraPages, setExtraPages] = useState<AuditPageResponse[]>([]);

  // Build the query string once so both the live query and the export-button share it.
  const buildParams = useCallback(
    (overrides?: Record<string, string>) => {
      const params = new URLSearchParams();
      if (action.trim()) params.set('action', action.trim());
      if (resourceType.trim()) params.set('resourceType', resourceType.trim());
      if (resourceId.trim()) params.set('resourceId', resourceId.trim());
      if (userId.trim()) params.set('userId', userId.trim());
      if (ipAddress.trim()) params.set('ipAddress', ipAddress.trim());
      if (since.trim()) params.set('since', since);
      if (until.trim()) params.set('until', until);
      if (overrides) for (const [k, v] of Object.entries(overrides)) params.set(k, v);
      return params;
    },
    [action, resourceType, resourceId, userId, ipAddress, since, until],
  );

  const queryKey = ['audit', action, resourceType, resourceId, userId, ipAddress, since, until, take];
  const { data: firstPage, refetch, isFetching } = useQuery({
    queryKey,
    queryFn: async () => {
      const params = buildParams();
      params.set('take', String(take));
      return await api.get<AuditPageResponse>(`/audit?${params.toString()}`);
    },
    refetchInterval: 15_000, // Poll every 15s — audit entries aren't time-critical, so near-live is enough
  });

  // Reset the accumulated pages whenever the filter set changes — old pages no longer
  // match the new filter, so keeping them would be misleading. This MUST live outside
  // queryFn: queryFn also fires on the 15s auto-refetch and on manual Refresh, both of
  // which must keep the "Load more"-extras intact. Tying the reset to the filter values
  // (not to the fetch) means refetch keeps the user's pagination, filter-change drops it.
   
  useEffect(() => { setExtraPages([]); }, [action, resourceType, resourceId, userId, ipAddress, since, until, take]);

  const entries: AuditEntry[] = useMemo(() => {
    const head = firstPage?.items ?? [];
    const rest = extraPages.flatMap((p) => p.items);
    return [...head, ...rest];
  }, [firstPage, extraPages]);

  const lastCursor: AuditCursor | null = useMemo(() => {
    if (extraPages.length > 0) return extraPages[extraPages.length - 1].nextCursor;
    return firstPage?.nextCursor ?? null;
  }, [firstPage, extraPages]);

  const loadMore = useCallback(async () => {
    if (!lastCursor) return;
    const params = buildParams();
    params.set('take', String(take));
    params.set('afterTs', lastCursor.timestamp);
    params.set('afterId', lastCursor.id);
    const next = await api.get<AuditPageResponse>(`/audit?${params.toString()}`);
    setExtraPages((prev) => [...prev, next]);
  }, [lastCursor, buildParams, take]);

  // Export-Links: the streaming endpoint reads the same filter params; browser attaches the
  // httpOnly auth cookie on the GET, Content-Disposition triggers the download. No fetch +
  // blob roundtrip needed since the response is already a file download.
  const exportHref = useCallback(
    (format: 'csv' | 'ndjson') => {
      const params = buildParams({ format });
      return `/api/audit/export?${params.toString()}`;
    },
    [buildParams],
  );

  // Group counter — surface the most frequent actions as quick-filter chips.
  const actionCounts = useMemo(() => {
    const c = new Map<string, number>();
    for (const e of entries) c.set(e.action, (c.get(e.action) ?? 0) + 1);
    return [...c.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8);
  }, [entries]);

  return (
    <div className="max-w-6xl mx-auto p-6 space-y-4 np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <p className="text-sm text-on-surface-variant mt-1">
          {t('audit:subtitle')}
        </p>

        {/* Export-Dropdown */}
        <div className="flex items-center gap-2">
          <div className="relative group">
            <button
              type="button"
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-surface-high hover:bg-surface-highest text-on-surface-variant text-xs font-label font-semibold transition-colors"
              title={t('audit:export.hint')}
            >
              <Download size={12} /> {t('audit:export.label')}
            </button>
            <div className="absolute right-0 top-full mt-1 hidden group-hover:block group-focus-within:block bg-surface-high border border-outline-variant/30 rounded-md shadow-lg z-10 min-w-[160px]">
              <a
                href={exportHref('csv')}
                className="block px-3 py-1.5 text-xs font-label hover:bg-surface-highest text-on-surface"
              >
                {t('audit:export.csv')}
              </a>
              <a
                href={exportHref('ndjson')}
                className="block px-3 py-1.5 text-xs font-label hover:bg-surface-highest text-on-surface"
              >
                {t('audit:export.ndjson')}
              </a>
            </div>
          </div>

          <button
            onClick={() => refetch()}
            className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-surface-high hover:bg-surface-highest text-on-surface-variant text-xs font-label font-semibold transition-colors ${isFetching ? 'opacity-60' : ''}`}
            title={t('common:reload')}
          >
            <Renew size={12} className={isFetching ? 'animate-spin' : ''} /> {t('audit:refresh')}
          </button>
        </div>
      </div>
      {/* Filter-Zeile */}
      <div className="np-card p-3 space-y-2">
        <div className="grid grid-cols-1 md:grid-cols-4 gap-2">
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.action')}</label>
            <div className="relative">
              <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-outline" />
              <input
                type="text"
                value={action}
                onChange={(e) => setAction(e.target.value)}
                placeholder="WORKFLOW_CREATED"
                className="input-field font-mono text-xs pl-7"
              />
            </div>
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.resourceType')}</label>
            <input
              type="text"
              value={resourceType}
              onChange={(e) => setResourceType(e.target.value)}
              placeholder={t('audit:resourcePlaceholder')}
              className="input-field text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.resourceId')}</label>
            <input
              type="text"
              value={resourceId}
              onChange={(e) => setResourceId(e.target.value)}
              placeholder="GUID"
              className="input-field font-mono text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.userId')}</label>
            <input
              type="text"
              value={userId}
              onChange={(e) => setUserId(e.target.value)}
              placeholder="GUID"
              className="input-field font-mono text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.ipAddress')}</label>
            <input
              type="text"
              value={ipAddress}
              onChange={(e) => setIpAddress(e.target.value)}
              placeholder="10.0.0.42"
              className="input-field font-mono text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.since')}</label>
            <input
              type="datetime-local"
              value={since}
              onChange={(e) => setSince(e.target.value)}
              className="input-field text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.until')}</label>
            <input
              type="datetime-local"
              value={until}
              onChange={(e) => setUntil(e.target.value)}
              className="input-field text-xs"
            />
          </div>
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-0.5">{t('audit:fields.maxRows')}</label>
            <select
              value={take}
              onChange={(e) => setTake(parseInt(e.target.value, 10))}
              className="input-field text-xs"
            >
              <option value={50}>50</option>
              <option value={100}>100</option>
              <option value={250}>250</option>
              <option value={500}>500</option>
            </select>
          </div>
        </div>

        {actionCounts.length > 0 && (
          <div className="flex flex-wrap gap-1 pt-1">
            <span className="text-[10px] font-label text-outline uppercase tracking-wider mr-1 self-center">{t('common:quickFilter')}</span>
            {actionCounts.map(([a, n]) => (
              <button
                key={a}
                onClick={() => setAction(a === action ? '' : a)}
                className={`text-[10px] font-mono px-1.5 py-0.5 rounded transition-colors ${
                  a === action
                    ? 'bg-primary text-on-primary'
                    : 'bg-surface-high text-on-surface-variant hover:bg-surface-highest'
                }`}
              >
                {a} <span className="opacity-60">({n})</span>
              </button>
            ))}
          </div>
        )}
      </div>
      {/* Entry-Liste */}
      <div className="np-card overflow-hidden">
        <div className="hidden lg:grid grid-cols-[160px_22px_minmax(180px,1fr)_minmax(120px,auto)_minmax(120px,auto)_minmax(140px,auto)_minmax(110px,auto)] gap-x-3 px-3 py-2 bg-surface-low text-[10px] font-label font-bold text-outline uppercase tracking-wider">
          <span>{t('audit:tableHeaders.timestamp')}</span>
          <span></span>
          <span>{t('audit:tableHeaders.action')}</span>
          <span>{t('audit:tableHeaders.resource')}</span>
          <span>{t('audit:tableHeaders.resourceId')}</span>
          <span>{t('audit:tableHeaders.user')}</span>
          <span>{t('audit:tableHeaders.ip')}</span>
        </div>
        {entries.length === 0 && (
          <div className="px-4 py-10 text-center font-label text-sm text-on-surface-variant">
            {t('audit:noEntries')}
          </div>
        )}
        {entries.map((e) => {
          const isOpen = expandedId === e.id;
          const details = e.details;
          const hasDetails = !!details && details !== '{}';
          // Username column wins over UserId truncation — frozen at write-time so deleted
          // users still show up as their original name. Falls back to short id for legacy
          // entries that predate the username column.
          const userDisplay = e.username ?? (e.userId ? e.userId.slice(0, 8) + '…' : '—');
          return (
            <div key={e.id}>
              {isMobile ? (
                <button
                  type="button"
                  onClick={() => setExpandedId(isOpen ? null : e.id)}
                  disabled={!hasDetails}
                  className={`w-full flex flex-col gap-1 px-3 py-2 text-left border-t border-outline/40 transition-colors ${
                    hasDetails ? 'hover:bg-surface-low cursor-pointer' : 'cursor-default'
                  } ${isOpen ? 'bg-surface-low' : ''}`}
                >
                  <div className="flex items-center gap-2">
                    {hasDetails ? (isOpen ? <ChevronDown size={12} className="text-outline shrink-0" /> : <ChevronRight size={12} className="text-outline shrink-0" />) : <span className="w-3 shrink-0" />}
                    <span className={`font-mono text-xs font-semibold ${actionColor(e.action)}`}>{e.action}</span>
                    <span className="ml-auto font-mono text-[10px] text-on-surface-variant tabular-nums shrink-0" title={e.timestamp}>
                      {formatDate(e.timestamp, { hour12: false })}
                    </span>
                  </div>
                  <dl className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-0.5 pl-5 text-[11px]">
                    <dt className="text-outline">{t('audit:tableHeaders.resource')}</dt>
                    <dd className="font-mono text-on-surface-variant truncate min-w-0">
                      {e.resourceType ?? '—'}{e.resourceId ? ` (${e.resourceId.slice(0, 8)}…)` : ''}
                    </dd>
                    <dt className="text-outline">{t('audit:tableHeaders.user')}</dt>
                    <dd className="font-label text-on-surface truncate min-w-0">{userDisplay}</dd>
                    <dt className="text-outline">{t('audit:tableHeaders.ip')}</dt>
                    <dd className="font-mono text-on-surface-variant truncate min-w-0">{e.ipAddress ?? '—'}</dd>
                  </dl>
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => setExpandedId(isOpen ? null : e.id)}
                  disabled={!hasDetails}
                  className={`w-full grid grid-cols-[160px_22px_minmax(180px,1fr)_minmax(120px,auto)_minmax(120px,auto)_minmax(140px,auto)_minmax(110px,auto)] gap-x-3 items-center px-3 py-1.5 text-left border-t border-outline/40 text-sm transition-colors ${
                    hasDetails ? 'hover:bg-surface-low cursor-pointer' : 'cursor-default'
                  } ${isOpen ? 'bg-surface-low' : ''}`}
                >
                  <span className="font-mono text-[11px] text-on-surface-variant tabular-nums" title={e.timestamp}>
                    {formatDate(e.timestamp, { hour12: false })}
                  </span>
                  {hasDetails ? (isOpen ? <ChevronDown size={12} className="text-outline" /> : <ChevronRight size={12} className="text-outline" />) : <span />}
                  <span className={`font-mono text-xs font-semibold ${actionColor(e.action)}`}>{e.action}</span>
                  <span className="font-mono text-[11px] text-on-surface-variant truncate">{e.resourceType ?? '—'}</span>
                  <span className="font-mono text-[10px] text-outline truncate" title={e.resourceId ?? ''}>
                    {e.resourceId ? e.resourceId.slice(0, 8) + '…' : '—'}
                  </span>
                  <span className="font-label text-[11px] text-on-surface truncate" title={e.userId ?? ''}>
                    {userDisplay}
                  </span>
                  <span className="font-mono text-[10px] text-on-surface-variant truncate" title={e.ipAddress ?? ''}>
                    {e.ipAddress ?? '—'}
                  </span>
                </button>
              )}
              {isOpen && hasDetails && (
                <pre className="px-14 pb-2 pt-1 bg-surface-low/40 border-t border-outline-variant/10 font-mono text-[11px] text-on-surface whitespace-pre-wrap break-words">
                  {prettyJson(details)}
                </pre>
              )}
            </div>
          );
        })}
      </div>
      {lastCursor && (
        <div className="flex justify-center">
          <button
            onClick={loadMore}
            className="px-4 py-1.5 rounded-md bg-surface-high hover:bg-surface-highest text-on-surface-variant text-xs font-label font-semibold transition-colors"
          >
            {t('audit:loadMore')}
          </button>
        </div>
      )}
      <p className="font-label text-[11px] text-on-surface-variant">
        {t('audit:showing', { count: entries.length })}{' '}
        {t('audit:retention')} <code className="font-mono">Retention:AuditLog:Enabled=true</code>.
      </p>
    </div>
  );
}

/** Farbkodierung nach dem Verb-Präfix — visuell grep-freundlich. */
function actionColor(action: string): string {
  if (action.endsWith('_CREATED') || action.endsWith('_SUCCESS') || action.endsWith('_RESUMED')) return 'text-green-700';
  if (action.endsWith('_DELETED') || action.endsWith('_FAILED')   || action.endsWith('_CANCELLED') || action.endsWith('_DEBUG_STOP')) return 'text-red-700';
  if (action.endsWith('_UPDATED') || action.endsWith('_ROLLED_BACK') || action.endsWith('_STEP_OVER') || action.endsWith('_CHANGED')) return 'text-amber-700';
  if (action.startsWith('LOGIN_') || action.startsWith('LOGOUT') || action.startsWith('TOKEN_')) return 'text-sky-700';
  if (action.endsWith('_DECRYPTED')) return 'text-violet-700';
  return 'text-on-surface';
}

function prettyJson(raw: string): string {
  try { return JSON.stringify(JSON.parse(raw), null, 2); }
  catch { return raw; }
}
