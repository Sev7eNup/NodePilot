import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Add, Apps, Certificate, Chemistry, CheckmarkFilled, ChevronDown, ChevronUp, CircleDash,
  Close, Edit, ErrorFilled, FlashFilled, Password, Search, SecurityServices, Tag, TrashCan,
  WifiController, WifiOff,
} from '@carbon/icons-react';
import { api } from '../api/client';
import { ModalShell } from '../components/common/ModalShell';
import { MobileCardList } from '../components/common/MobileCardList';
import type { ManagedMachine, Credential } from '../types/api';
import { formatRelative, formatDate } from '../lib/format';
import { useRole } from '../lib/rbac';
import { useIsMobile } from '../hooks/useMediaQuery';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';

type DialogMode =
  | { kind: 'create' }
  | { kind: 'edit'; machine: ManagedMachine }
  | null;

// ColKey covers all columns (sort uses it). ResizableColKey excludes `tags` —
// tags is the auto-flex column (wraps chips, benefits from extra width) and
// absorbs whatever's left over after the fixed columns claim their pixels.
// Name and friends are short uniform identifiers, so making *them* the flex
// column would just give us 800 px of whitespace. Picking the chip-list column
// instead lets the table fill the viewport without any single cell looking
// stretched.
type ColKey =
  | 'name' | 'status' | 'live' | 'workflows' | 'activity'
  | 'hostname' | 'credential' | 'tags' | 'lastCheck';
type ResizableColKey = Exclude<ColKey, 'tags'>;
type TestState = { status: 'idle' | 'running' | 'success' | 'failure'; message?: string };

const ACTIONS_WIDTH = 120; // 3 buttons × ~28px + gap-1 + px-4 cell padding
const TAGS_MIN_WIDTH = 180; // floor so the chip cell stays usable on narrow viewports
const DEFAULT_WIDTHS: Record<ResizableColKey, number> = {
  name: 220, status: 160, live: 110, workflows: 110, activity: 150,
  hostname: 200, credential: 180, lastCheck: 150,
};
// Per-column resize minimum. Status must hold the German worst-case "Fehlgeschlagen"
// badge (text-xs), otherwise the nowrap pill clips once you shrink the column.
// Everything else falls back to the global floor of 50.
const MIN_WIDTHS: Partial<Record<ResizableColKey, number>> = { status: 160 };
const DEFAULT_PORT_HTTP = 5985;
const DEFAULT_PORT_HTTPS = 5986;

function splitTags(tags: string | null | undefined): string[] {
  if (!tags) return [];
  return tags.split(',').map((t) => t.trim()).filter(Boolean);
}

export function MachinesPage() {
  const { t } = useTranslation(['machines', 'common']);
  const queryClient = useQueryClient();
  const { canWrite, canDelete } = useRole();
  const isMobile = useIsMobile();

  const [dialog, setDialog] = useState<DialogMode>(null);
  const [search, setSearch] = useState('');
  const [tagFilter, setTagFilter] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<ColKey | null>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [testState, setTestState] = useState<Record<string, TestState>>({});

  // --- Column resizing (same pattern as WorkflowsPage). Tags is excluded —
  // it's the auto-flex column, so it has no inline width and no drag-handle.
  const [colWidths, setColWidths] = useState(DEFAULT_WIDTHS);
  // Minimum total table width — used to trigger horizontal scroll on narrow
  // viewports before the flex Tags column gets squeezed below readability.
  const tableMinWidth = useMemo(
    () => Object.values(colWidths).reduce((a, b) => a + b, 0) + ACTIONS_WIDTH + TAGS_MIN_WIDTH,
    [colWidths],
  );
  const resizeRef = useRef<{ col: ResizableColKey; startX: number; startWidth: number } | null>(null);

  const startResize = (col: ResizableColKey, e: React.MouseEvent) => {
    e.preventDefault();
    resizeRef.current = { col, startX: e.clientX, startWidth: colWidths[col] };
    const onMove = (ev: MouseEvent) => {
      if (!resizeRef.current) return;
      const { col, startWidth, startX } = resizeRef.current;
      const w = Math.max(MIN_WIDTHS[col] ?? 50, startWidth + ev.clientX - startX);
      setColWidths((prev) => ({ ...prev, [col]: w }));
    };
    const onUp = () => {
      resizeRef.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  };

  const handleSort = (col: ColKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(col); setSortDir('asc'); }
  };

  const { data: machines, isLoading } = useQuery({
    queryKey: ['machines'],
    queryFn: () => api.get<ManagedMachine[]>('/machines'),
  });

  const { data: credentials } = useQuery({
    queryKey: ['credentials'],
    queryFn: () => api.get<Credential[]>('/credentials'),
  });

  const credentialById = useMemo(() => {
    const map = new Map<string, Credential>();
    (credentials ?? []).forEach((c) => map.set(c.id, c));
    return map;
  }, [credentials]);

  const allTags = useMemo(() => {
    const set = new Set<string>();
    (machines ?? []).forEach((m) => splitTags(m.tags).forEach((tag) => set.add(tag)));
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }, [machines]);

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/machines/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['machines'] }),
    onError: (err: Error) => toast.error(t('common:deleteFailed', { message: err.message })),
  });

  const testMutation = useMutation({
    mutationFn: async (id: string) => {
      setTestState((prev) => ({ ...prev, [id]: { status: 'running' } }));
      const result = await api.post<{ success: boolean; computerName?: string; error?: string }>(
        `/machines/${id}/test`,
      );
      return { id, result };
    },
    onSuccess: ({ id, result }) => {
      setTestState((prev) => ({
        ...prev,
        [id]: {
          status: result.success ? 'success' : 'failure',
          message: result.computerName || result.error || '',
        },
      }));
      queryClient.invalidateQueries({ queryKey: ['machines'] });
    },
    onError: (err: Error, id) => {
      setTestState((prev) => ({ ...prev, [id]: { status: 'failure', message: err.message } }));
    },
  });

  const filteredSorted = useMemo(() => {
    let list = machines ?? [];
    const term = search.trim().toLowerCase();
    if (term) {
      list = list.filter((m) =>
        m.name.toLowerCase().includes(term)
        || m.hostname.toLowerCase().includes(term)
        || (m.tags ?? '').toLowerCase().includes(term),
      );
    }
    if (tagFilter) {
      list = list.filter((m) => splitTags(m.tags).includes(tagFilter));
    }
    if (!sortBy) return list;
    return [...list].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':       cmp = a.name.localeCompare(b.name); break;
        case 'hostname':   cmp = a.hostname.localeCompare(b.hostname); break;
        case 'status':     cmp = Number(b.isReachable) - Number(a.isReachable); break;
        case 'live':       cmp = a.activeRunCount - b.activeRunCount; break;
        case 'workflows':  cmp = a.usedByWorkflowCount - b.usedByWorkflowCount; break;
        case 'activity': {
          // Sort by success rate, but treat "no data" as worst so machines with
          // actual usage rise above untouched ones when sorting desc.
          const ra = a.recentStepCount > 0 ? (a.recentStepCount - a.recentFailedStepCount) / a.recentStepCount : -1;
          const rb = b.recentStepCount > 0 ? (b.recentStepCount - b.recentFailedStepCount) / b.recentStepCount : -1;
          cmp = ra - rb; break;
        }
        case 'lastCheck':  cmp = (a.lastConnectivityCheck ?? '').localeCompare(b.lastConnectivityCheck ?? ''); break;
        case 'credential': {
          const ca = a.defaultCredentialId ? credentialById.get(a.defaultCredentialId)?.name ?? '' : '';
          const cb = b.defaultCredentialId ? credentialById.get(b.defaultCredentialId)?.name ?? '' : '';
          cmp = ca.localeCompare(cb); break;
        }
        case 'tags':       cmp = (a.tags ?? '').localeCompare(b.tags ?? ''); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [machines, search, tagFilter, sortBy, sortDir, credentialById]);

  const totalCount = machines?.length ?? 0;
  const reachableCount = (machines ?? []).filter((m) => m.isReachable).length;

  const testAll = async () => {
    if (!machines || machines.length === 0) return;
    if (!(await confirmDialog(t('machines:testAllConfirm', { count: machines.length })))) return;
    machines.forEach((m) => testMutation.mutate(m.id));
  };

  return (
    <div className="max-w-[1600px] mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          {totalCount > 0 && (
            <p className="text-sm text-on-surface-variant mt-1">
              {t('machines:summary', { reachable: reachableCount, total: totalCount })}
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          {canWrite && totalCount > 0 && (
            <button
              onClick={testAll}
              className="flex items-center gap-2 px-3 py-2 bg-surface-lowest border border-outline-variant text-on-surface rounded-md hover:bg-surface-low transition-colors text-sm"
              title={t('machines:testAllTitle')}
            >
              <Chemistry size={16} /> <span className="hidden sm:inline">{t('machines:testAll')}</span>
            </button>
          )}
          {canWrite && (
            <button
              onClick={() => setDialog({ kind: 'create' })}
              title={t('machines:addMachine')}
              className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors text-sm"
            >
              <Add size={16} /> <span className="hidden sm:inline">{t('machines:addMachine')}</span>
            </button>
          )}
        </div>
      </div>

      {/* Toolbar: search + tag-filter chips. Same full-container width as the
          table below — both surfaces stretch edge-to-edge like every other admin
          view in the app. */}
      {totalCount > 0 && (
        <div className="np-card p-3 mb-3 flex flex-wrap items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('machines:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          {allTags.length > 0 && (
            <div className="flex flex-wrap items-center gap-1.5">
              <Tag size={13} className="text-outline" />
              <button
                onClick={() => setTagFilter(null)}
                className={`px-2 py-0.5 rounded text-xs font-medium ${
                  tagFilter === null
                    ? 'bg-blue-600 text-white'
                    : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'
                }`}
              >
                {t('machines:tagFilterAll')}
              </button>
              {allTags.map((tag) => (
                <button
                  key={tag}
                  onClick={() => setTagFilter((current) => (current === tag ? null : tag))}
                  className={`px-2 py-0.5 rounded text-xs font-medium ${
                    tagFilter === tag
                      ? 'bg-blue-600 text-white'
                      : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'
                  }`}
                >
                  {tag}
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : !machines || machines.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">
          {t('machines:noMachines')}
        </div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">
          {t('machines:noMatch')}
        </div>
      ) : isMobile ? (
        <MobileCardList
          items={filteredSorted}
          getKey={(m) => m.id}
          renderTitle={(m) => (
            <div className="flex items-center gap-2 min-w-0">
              <span className="text-sm font-semibold text-on-surface truncate">{m.name}</span>
              {m.useSsl && (
                <span
                  className="inline-flex items-center gap-1 text-[10px] font-medium text-emerald-600 dark:text-emerald-400 bg-emerald-500/15 rounded-full px-1.5 py-0.5 shrink-0"
                  title={t('machines:sslOnTitle')}
                >
                  <Certificate size={10} /> {t('machines:sslOn')}
                </span>
              )}
            </div>
          )}
          renderFields={(m) => {
            const cred = m.defaultCredentialId ? credentialById.get(m.defaultCredentialId) : null;
            const tags = splitTags(m.tags);
            const state = testState[m.id];
            return [
              { label: t('machines:tableHeaders.status'), value: <StatusBadge isReachable={m.isReachable} test={state} t={t} /> },
              { label: t('machines:tableHeaders.live'), value: <LiveCell count={m.activeRunCount} t={t} /> },
              { label: t('machines:tableHeaders.workflows'), value: <WorkflowsCell count={m.usedByWorkflowCount} t={t} /> },
              { label: t('machines:tableHeaders.activity'), value: <ActivityCell total={m.recentStepCount} failed={m.recentFailedStepCount} t={t} /> },
              { label: t('machines:tableHeaders.hostname'), value: <span className="text-sm text-on-surface-variant font-mono break-all">{m.hostname}:{m.winRmPort}</span> },
              {
                label: t('machines:tableHeaders.credential'),
                value: cred ? (
                  <span className="inline-flex items-center gap-1 text-sm text-on-surface-variant min-w-0">
                    <Password size={12} className="text-outline shrink-0" />
                    <span className="truncate" title={t('machines:credentialOption', { name: cred.name, username: cred.username })}>{cred.name}</span>
                  </span>
                ) : (
                  <span className="text-xs text-outline italic">{t('machines:noDefaultCredential')}</span>
                ),
              },
              {
                label: t('machines:tableHeaders.tags'),
                value: tags.length > 0 ? (
                  <div className="flex flex-wrap gap-1">
                    {tags.map((tag) => (
                      <button
                        key={tag}
                        onClick={() => setTagFilter((current) => (current === tag ? null : tag))}
                        className="px-1.5 py-0.5 bg-surface-container text-on-surface-variant rounded text-[11px] hover:bg-surface-high"
                        title={t('machines:filterByTag', { tag })}
                      >
                        {tag}
                      </button>
                    ))}
                  </div>
                ) : (
                  <span className="text-xs text-outline">{t('common:dash')}</span>
                ),
              },
              {
                label: t('machines:tableHeaders.lastCheck'),
                value: m.lastConnectivityCheck ? (
                  <span className="text-xs text-on-surface-variant" title={formatDate(m.lastConnectivityCheck)}>{formatRelative(m.lastConnectivityCheck)}</span>
                ) : (
                  <span className="text-xs text-outline">{t('machines:neverChecked')}</span>
                ),
              },
            ];
          }}
          renderActions={(m) => {
            const state = testState[m.id];
            return (
              <>
                {canWrite && (
                  <button
                    onClick={() => testMutation.mutate(m.id)}
                    disabled={state?.status === 'running'}
                    className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg disabled:opacity-40"
                    title={t('machines:testConnection')}
                  >
                    <Chemistry size={16} />
                  </button>
                )}
                {canWrite && (
                  <button
                    onClick={() => setDialog({ kind: 'edit', machine: m })}
                    className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg"
                    title={t('machines:editTitle')}
                  >
                    <Edit size={16} />
                  </button>
                )}
                {canDelete && (
                  <button
                    onClick={async () => {
                      if (await confirmDialog({ message: t('machines:deleteConfirmName', { name: m.name }), danger: true }))
                        deleteMutation.mutate(m.id);
                    }}
                    className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg"
                    title={t('common:delete')}
                  >
                    <TrashCan size={16} />
                  </button>
                )}
              </>
            );
          }}
        />
      ) : (
        <div className="np-card overflow-hidden"><div className="overflow-x-auto">
          <table
            style={{
              tableLayout: 'fixed',
              width: '100%',
              // Floor for horizontal scroll: kicks in before Tags is squeezed
              // below readability when the viewport is too narrow.
              minWidth: tableMinWidth,
            }}
          >
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                {/* Fixed-width sortable + resizable columns. Each declares its
                    width inline; Tags (rendered after this loop) gets whatever's
                    left over because it has no explicit width under fixed layout.
                    Order groups the operational signals (status / live / workflows
                    / activity) together right after the name so an operator scanning
                    the list reads identity → health → usage in one left-to-right pass. */}
                {([
                  ['name', t('machines:tableHeaders.name')],
                  ['status', t('machines:tableHeaders.status')],
                  ['live', t('machines:tableHeaders.live')],
                  ['workflows', t('machines:tableHeaders.workflows')],
                  ['activity', t('machines:tableHeaders.activity')],
                  ['hostname', t('machines:tableHeaders.hostname')],
                  ['credential', t('machines:tableHeaders.credential')],
                ] as [ResizableColKey, string][]).map(([col, label]) => (
                  <th key={col} style={{ width: colWidths[col] }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                    <button
                      onClick={() => handleSort(col)}
                      className="flex items-center gap-1 hover:text-on-surface transition-colors"
                    >
                      {label}
                      {sortBy === col
                        ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                        : <span className="w-3" />}
                    </button>
                    <div
                      onMouseDown={(e) => startResize(col, e)}
                      className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                    />
                  </th>
                ))}
                {/* Tags = auto-flex. No explicit width, no resize handle — it
                    absorbs whatever horizontal space the fixed columns leave. */}
                <th style={{ minWidth: TAGS_MIN_WIDTH }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                  <button
                    onClick={() => handleSort('tags')}
                    className="flex items-center gap-1 hover:text-on-surface transition-colors"
                  >
                    {t('machines:tableHeaders.tags')}
                    {sortBy === 'tags'
                      ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                      : <span className="w-3" />}
                  </button>
                </th>
                <th style={{ width: colWidths.lastCheck }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                  <button
                    onClick={() => handleSort('lastCheck')}
                    className="flex items-center gap-1 hover:text-on-surface transition-colors"
                  >
                    {t('machines:tableHeaders.lastCheck')}
                    {sortBy === 'lastCheck'
                      ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                      : <span className="w-3" />}
                  </button>
                  <div
                    onMouseDown={(e) => startResize('lastCheck', e)}
                    className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                  />
                </th>
                <th style={{ width: ACTIONS_WIDTH }} className="px-4 py-2 text-left">{t('machines:tableHeaders.actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline/30">
              {filteredSorted.map((m) => {
                const cred = m.defaultCredentialId ? credentialById.get(m.defaultCredentialId) : null;
                const tags = splitTags(m.tags);
                const state = testState[m.id];
                return (
                  <tr key={m.id} className="hover:bg-surface-low">
                    <td className="px-4 py-2 overflow-hidden">
                      <div className="flex items-center gap-2 min-w-0">
                        <span className="text-sm font-semibold text-on-surface-variant truncate">{m.name}</span>
                        {m.useSsl && (
                          <span
                            className="inline-flex items-center gap-1 text-[10px] font-medium text-emerald-600 dark:text-emerald-400 bg-emerald-500/15 rounded-full px-1.5 py-0.5 shrink-0"
                            title={t('machines:sslOnTitle')}
                          >
                            <Certificate size={10} /> {t('machines:sslOn')}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <StatusBadge isReachable={m.isReachable} test={state} t={t} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <LiveCell count={m.activeRunCount} t={t} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <WorkflowsCell count={m.usedByWorkflowCount} t={t} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <ActivityCell
                        total={m.recentStepCount}
                        failed={m.recentFailedStepCount}
                        t={t}
                      />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <span className="text-sm text-on-surface-variant font-mono truncate block">
                        {m.hostname}:{m.winRmPort}
                      </span>
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      {cred ? (
                        <span className="inline-flex items-center gap-1 text-sm text-on-surface-variant min-w-0">
                          <Password size={12} className="text-outline shrink-0" />
                          <span
                            className="truncate"
                            title={t('machines:credentialOption', { name: cred.name, username: cred.username })}
                          >
                            {cred.name}
                          </span>
                        </span>
                      ) : (
                        <span className="text-xs text-outline italic">{t('machines:noDefaultCredential')}</span>
                      )}
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      {tags.length > 0 ? (
                        <div className="flex flex-wrap gap-1">
                          {tags.map((tag) => (
                            <button
                              key={tag}
                              onClick={() => setTagFilter((current) => (current === tag ? null : tag))}
                              className="px-1.5 py-0.5 bg-surface-container text-on-surface-variant rounded text-[11px] hover:bg-surface-high"
                              title={t('machines:filterByTag', { tag })}
                            >
                              {tag}
                            </button>
                          ))}
                        </div>
                      ) : (
                        <span className="text-xs text-outline">{t('common:dash')}</span>
                      )}
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      {m.lastConnectivityCheck ? (
                        <span
                          className="text-xs text-on-surface-variant"
                          title={formatDate(m.lastConnectivityCheck)}
                        >
                          {formatRelative(m.lastConnectivityCheck)}
                        </span>
                      ) : (
                        <span className="text-xs text-outline">{t('machines:neverChecked')}</span>
                      )}
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <div className="flex items-center gap-1 whitespace-nowrap">
                        {canWrite && (
                          <button
                            onClick={() => testMutation.mutate(m.id)}
                            disabled={state?.status === 'running'}
                            className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg disabled:opacity-40"
                            title={t('machines:testConnection')}
                          >
                            <Chemistry size={16} />
                          </button>
                        )}
                        {canWrite && (
                          <button
                            onClick={() => setDialog({ kind: 'edit', machine: m })}
                            className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg"
                            title={t('machines:editTitle')}
                          >
                            <Edit size={16} />
                          </button>
                        )}
                        {canDelete && (
                          <button
                            onClick={async () => {
                              if (await confirmDialog({ message: t('machines:deleteConfirmName', { name: m.name }), danger: true }))
                                deleteMutation.mutate(m.id);
                            }}
                            className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg"
                            title={t('common:delete')}
                          >
                            <TrashCan size={16} />
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div></div>
      )}

      {dialog?.kind === 'create' && (
        <MachineDialog
          credentials={credentials ?? []}
          onCancel={() => setDialog(null)}
          onSaved={() => {
            queryClient.invalidateQueries({ queryKey: ['machines'] });
            setDialog(null);
          }}
        />
      )}
      {dialog?.kind === 'edit' && (
        <MachineDialog
          machine={dialog.machine}
          credentials={credentials ?? []}
          onCancel={() => setDialog(null)}
          onSaved={() => {
            queryClient.invalidateQueries({ queryKey: ['machines'] });
            setDialog(null);
          }}
        />
      )}
    </div>
  );
}

/**
 * Workflows-using-this-machine count. Mirrors the activity-count cell style on
 * WorkflowsPage so the two surfaces feel like siblings, and drops to a dash when
 * unused (less noisy than a 0 badge — orphan machines should fade, not glow).
 */
function WorkflowsCell({
  count, t,
}: Readonly<{ count: number; t: (k: string, opts?: Record<string, unknown>) => string }>) {
  if (count === 0) {
    return <span className="text-xs text-outline" title={t('machines:workflowsZeroTitle')}>{t('common:dash')}</span>;
  }
  return (
    <div
      className="flex items-center gap-1.5 text-sm text-on-surface-variant"
      title={t('machines:workflowsCountTitle', { count })}
    >
      <Apps size={14} className="text-outline" />
      <span className="tabular-nums">{count}</span>
    </div>
  );
}

/**
 * 7-day step throughput with success-rate bar. Same visual rhythm as
 * SuccessRateCell on WorkflowsPage — total count + a small progress bar where
 * the green portion = success ratio. A failure-rich row reads red at a glance,
 * a healthy host reads green; idle hosts (no recent runs) render as a dash so
 * they don't compete for attention.
 */
function ActivityCell({
  total, failed, t,
}: Readonly<{ total: number; failed: number; t: (k: string, opts?: Record<string, unknown>) => string }>) {
  if (total === 0) {
    return <span className="text-xs text-outline">{t('common:dash')}</span>;
  }
  const success = total - failed;
  const pct = Math.round((success / total) * 100);
  const barClass = pct >= 95 ? 'np-bar-ok' : pct >= 80 ? 'np-bar-warn' : 'np-bar-bad';
  return (
    <div className="flex items-center gap-2" title={t('machines:activityTitle', { success, total, pct })}>
      <div className="w-12 h-1.5 bg-surface-container rounded-full overflow-hidden">
        <div className={`np-bar h-full ${barClass}`} style={{ width: `${pct}%` }} />
      </div>
      <span className="text-xs text-on-surface-variant tabular-nums">{success}/{total}</span>
    </div>
  );
}

/**
 * Currently-running step executions targeting this machine. Renders nothing
 * (just a dash) when idle so the column stays quiet — but lights up blue with
 * a spinning indicator the moment any workflow touches the host. That's the
 * "don't reboot this thing right now" signal during incident response.
 */
function LiveCell({
  count, t,
}: Readonly<{ count: number; t: (k: string, opts?: Record<string, unknown>) => string }>) {
  if (count === 0) {
    return <span className="text-xs text-outline">{t('common:dash')}</span>;
  }
  return (
    <span
      className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-blue-500/15 text-blue-600 dark:text-blue-400"
      title={t('machines:liveTitle', { count })}
    >
      <FlashFilled size={11} className="text-blue-600" />
      {t('machines:liveBadge', { count })}
    </span>
  );
}

function StatusBadge({
  isReachable, test, t,
}: Readonly<{
  isReachable: boolean;
  test: TestState | undefined;
  t: (k: string) => string;
}>) {
  // If a test just ran for this row, prefer that result over the cached
  // IsReachable — the user just clicked Test and expects to see what happened.
  if (test?.status === 'running') {
    return (
      <span className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-blue-500/15 text-blue-600 dark:text-blue-400">
        <CircleDash size={11} className="animate-spin" />
        {t('machines:statusTesting')}
      </span>
    );
  }
  if (test?.status === 'success') {
    return (
      <span
        className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-green-500/15 text-green-600 dark:text-green-400"
        title={test.message}
      >
        <CheckmarkFilled size={11} />
        {t('machines:statusOnline')}
      </span>
    );
  }
  if (test?.status === 'failure') {
    return (
      <span
        className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-red-500/15 text-red-600 dark:text-red-400"
        title={test.message}
      >
        <ErrorFilled size={11} />
        {t('machines:statusFailed')}
      </span>
    );
  }
  if (isReachable) {
    return (
      <span className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-green-500/15 text-green-600 dark:text-green-400">
        <WifiController size={11} className="text-green-500" />
        {t('machines:statusOnline')}
      </span>
    );
  }
  return (
    <span className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap px-2 py-0.5 rounded text-xs font-medium bg-surface-container text-on-surface-variant">
      <WifiOff size={11} className="text-surface-highest" />
      {t('machines:statusUnknown')}
    </span>
  );
}

/**
 * Combined create + edit dialog. When `machine` is provided we're editing —
 * the PUT endpoint replaces every field (no partial updates), so the form
 * mirrors that semantic by submitting all of them.
 */
function MachineDialog({
  machine, credentials, onCancel, onSaved,
}: Readonly<{
  machine?: ManagedMachine;
  credentials: Credential[];
  onCancel: () => void;
  onSaved: () => void;
}>) {
  const { t } = useTranslation(['machines', 'common']);
  const isEdit = !!machine;

  const [name, setName] = useState(machine?.name ?? '');
  const [hostname, setHostname] = useState(machine?.hostname ?? '');
  const [winRmPort, setWinRmPort] = useState<number>(machine?.winRmPort ?? DEFAULT_PORT_HTTP);
  const [useSsl, setUseSsl] = useState<boolean>(machine?.useSsl ?? false);
  const [defaultCredentialId, setDefaultCredentialId] = useState<string>(machine?.defaultCredentialId ?? '');
  const [tags, setTags] = useState<string>(machine?.tags ?? '');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  // QoL: flipping SSL bumps the default port (5985 ↔ 5986) only if the user
  // hasn't typed a custom port — otherwise we'd clobber their explicit value.
  const handleSslToggle = (next: boolean) => {
    setUseSsl(next);
    if (winRmPort === DEFAULT_PORT_HTTP && next) setWinRmPort(DEFAULT_PORT_HTTPS);
    else if (winRmPort === DEFAULT_PORT_HTTPS && !next) setWinRmPort(DEFAULT_PORT_HTTP);
  };

  const submit = async () => {
    if (!name.trim() || !hostname.trim()) {
      setError(t('machines:nameAndHostnameRequired'));
      return;
    }
    if (!Number.isInteger(winRmPort) || winRmPort < 1 || winRmPort > 65535) {
      setError(t('machines:invalidPort'));
      return;
    }
    setError(null);
    setPending(true);
    try {
      const body = {
        name: name.trim(),
        hostname: hostname.trim(),
        winRmPort,
        useSsl,
        defaultCredentialId: defaultCredentialId || null,
        tags: tags.trim() || null,
      };
      if (isEdit && machine) {
        await api.put(`/machines/${machine.id}`, body);
      } else {
        await api.post('/machines', body);
      }
      onSaved();
    } catch (err) {
      setError((err as Error).message);
      setPending(false);
    }
  };

  return (
    <ModalShell onClose={onCancel} maxWidth="max-w-lg">
        <div className="flex items-center justify-between mb-4">
          <h3 className="text-lg font-semibold text-on-surface">
            {isEdit ? t('machines:editTitleNamed', { name: machine?.name }) : t('machines:addMachine')}
          </h3>
          <button
            onClick={onCancel}
            className="p-1 text-on-surface-variant hover:bg-surface-container rounded"
            title={t('common:close')}
            aria-label={t('common:close')}
          >
            <Close size={16} />
          </button>
        </div>

        <div className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">
              {t('machines:fields.name')}
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={t('machines:displayName')}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              autoFocus
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">
              {t('machines:fields.hostname')}
            </label>
            <input
              type="text"
              value={hostname}
              onChange={(e) => setHostname(e.target.value)}
              placeholder={t('machines:hostnameOrIp')}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-medium text-on-surface-variant mb-1">
                {t('machines:fields.port')}
              </label>
              <input
                type="number"
                value={Number.isFinite(winRmPort) ? winRmPort : ''}
                onChange={(e) => {
                  const v = e.target.value;
                  setWinRmPort(v === '' ? Number.NaN : parseInt(v, 10));
                }}
                placeholder={t('machines:winRmPort')}
                min={1}
                max={65535}
                className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>
            <div className="flex flex-col">
              <label className="block text-xs font-medium text-on-surface-variant mb-1">
                {t('machines:fields.transport')}
              </label>
              <button
                type="button"
                onClick={() => handleSslToggle(!useSsl)}
                className={`flex items-center justify-center gap-2 px-3 py-2 border rounded-md text-sm transition-colors ${
                  useSsl
                    ? 'border-emerald-600 bg-emerald-50 text-emerald-700'
                    : 'border-outline-variant bg-surface-lowest text-on-surface-variant hover:bg-surface-low'
                }`}
                title={useSsl ? t('machines:sslOnTitle') : t('machines:sslOffTitle')}
              >
                {useSsl ? <Certificate size={14} /> : <SecurityServices size={14} />}
                {useSsl ? t('machines:sslOn') : t('machines:sslOff')}
              </button>
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">
              {t('machines:fields.credential')}
            </label>
            <select
              value={defaultCredentialId}
              onChange={(e) => setDefaultCredentialId(e.target.value)}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="">{t('machines:noDefaultCredential')}</option>
              {credentials.map((c) => (
                <option key={c.id} value={c.id}>
                  {t('machines:credentialOption', { name: c.name, username: c.username })}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">
              {t('machines:fields.tags')}
            </label>
            <input
              type="text"
              value={tags}
              onChange={(e) => setTags(e.target.value)}
              placeholder={t('machines:tagsPlaceholder')}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="text-[11px] text-outline mt-0.5">{t('machines:tagsHint')}</p>
          </div>
        </div>

        {error && <p className="text-sm text-red-600 mt-3">{error}</p>}

        <div className="flex justify-end gap-2 mt-5">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
          >
            {t('common:cancel')}
          </button>
          <button
            onClick={submit}
            disabled={pending}
            className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50"
          >
            {pending ? t('common:saving') : (isEdit ? t('common:save') : t('machines:addAction'))}
          </button>
        </div>
    </ModalShell>
  );
}
