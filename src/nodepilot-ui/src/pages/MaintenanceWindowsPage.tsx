import {
  Add,
  CalendarHeatMap,
  CalendarSettings,
  ChevronDown,
  ChevronUp,
  Edit,
  Search,
  TrashCan,
} from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { ModalShell } from '../components/common/ModalShell';
import { MobileCardList } from '../components/common/MobileCardList';
import { sharedFoldersApi } from '../api/sharedFolders';
import { useRole } from '../lib/rbac';
import { useIsMobile } from '../hooks/useMediaQuery';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';

/**
 * Admin/Operator view of maintenance windows. A window gates when its targeted workflows may
 * start: a Blackout blocks runs while active, AllowOnly permits runs only while active. Targeting
 * is Global, a set of folders (incl. descendants), or a set of workflows. Admin-only to mutate.
 */
type TargetKind = 'Folder' | 'Workflow';
type Mode = 'Blackout' | 'AllowOnly';
type Scope = 'Global' | 'Folders' | 'Workflows';
type Recurrence = 'OneTime' | 'Weekly' | 'Cron';

type Target = { targetKind: TargetKind; targetId: string };

type MaintenanceWindow = {
  id: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  mode: Mode;
  scopeKind: Scope;
  recurrence: Recurrence;
  oneTimeStartUtc: string | null;
  oneTimeEndUtc: string | null;
  weeklyDaysMask: number;
  weeklyStartMinuteOfDay: number | null;
  weeklyEndMinuteOfDay: number | null;
  cronExpression: string | null;
  durationMinutes: number | null;
  timeZoneId: string;
  targets: Target[];
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
};

type FormState = {
  id: string | null;
  name: string;
  description: string;
  isEnabled: boolean;
  mode: Mode;
  scope: Scope;
  recurrence: Recurrence;
  timeZoneId: string;
  oneTimeStart: string;
  oneTimeEnd: string;
  daysMask: number;
  startTime: string;
  endTime: string;
  cronExpression: string;
  durationMinutes: string;
  folderIds: string[];
  workflowIds: string[];
};

const DAY_KEYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function browserTz(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; }
  catch { return 'UTC'; }
}

const emptyForm = (): FormState => ({
  id: null, name: '', description: '', isEnabled: true,
  mode: 'Blackout', scope: 'Global', recurrence: 'Weekly',
  timeZoneId: browserTz(),
  oneTimeStart: '', oneTimeEnd: '',
  daysMask: 0, startTime: '22:00', endTime: '02:00',
  cronExpression: '', durationMinutes: '60',
  folderIds: [], workflowIds: [],
});

const minuteToHhmm = (m: number | null): string =>
  m == null ? '--:--' : `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const hhmmToMinute = (s: string): number | null => {
  const m = /^(\d{1,2}):(\d{2})$/.exec(s.trim());
  if (!m) return null;
  const h = Number(m[1]), min = Number(m[2]);
  return h >= 0 && h <= 23 && min >= 0 && min <= 59 ? h * 60 + min : null;
};
// UTC ISO <-> the `datetime-local` input value (which is wall-clock local time).
const isoToLocalInput = (iso: string | null): string => {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};
const localInputToIso = (local: string): string | null =>
  local ? new Date(local).toISOString() : null;

const daysFromMask = (mask: number, dayLabels: string[]): string => {
  const parts: string[] = [];
  for (let i = 0; i < 7; i++) if ((mask & (1 << i)) !== 0) parts.push(dayLabels[i]);
  return parts.length ? parts.join(', ') : '-';
};

export function MaintenanceWindowsPage() {
  const { t } = useTranslation(['maintenance', 'common']);
  const queryClient = useQueryClient();
  const { canAdmin } = useRole();
  const isMobile = useIsMobile();
  const [form, setForm] = useState<FormState>(emptyForm);
  const [showDialog, setShowDialog] = useState(false);
  const [search, setSearch] = useState('');

  const { data: windows, isLoading } = useQuery({
    queryKey: ['maintenance-windows'],
    queryFn: () => api.get<MaintenanceWindow[]>('/maintenance-windows'),
  });
  const { data: folders } = useQuery({ queryKey: ['shared-folders'], queryFn: () => sharedFoldersApi.list() });
  const { data: workflows } = useQuery({
    queryKey: ['workflows-min'],
    queryFn: () => api.get<Array<{ id: string; name: string }>>('/workflows'),
  });

  const buildBody = (f: FormState) => ({
    name: f.name.trim(),
    description: f.description.trim() || null,
    isEnabled: f.isEnabled,
    mode: f.mode,
    scopeKind: f.scope,
    recurrence: f.recurrence,
    oneTimeStartUtc: f.recurrence === 'OneTime' ? localInputToIso(f.oneTimeStart) : null,
    oneTimeEndUtc: f.recurrence === 'OneTime' ? localInputToIso(f.oneTimeEnd) : null,
    weeklyDaysMask: f.recurrence === 'Weekly' ? f.daysMask : 0,
    weeklyStartMinuteOfDay: f.recurrence === 'Weekly' ? hhmmToMinute(f.startTime) : null,
    weeklyEndMinuteOfDay: f.recurrence === 'Weekly' ? hhmmToMinute(f.endTime) : null,
    cronExpression: f.recurrence === 'Cron' ? f.cronExpression.trim() : null,
    durationMinutes: f.recurrence === 'Cron' ? Number(f.durationMinutes) : null,
    timeZoneId: f.timeZoneId.trim() || 'UTC',
    targets:
      f.scope === 'Folders' ? f.folderIds.map((id) => ({ targetKind: 'Folder', targetId: id }))
      : f.scope === 'Workflows' ? f.workflowIds.map((id) => ({ targetKind: 'Workflow', targetId: id }))
      : [],
  });

  const saveMutation = useMutation({
    mutationFn: async (f: FormState) => {
      const body = buildBody(f);
      if (f.id) await api.put(`/maintenance-windows/${f.id}`, body);
      else await api.post('/maintenance-windows', body);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['maintenance-windows'] });
      setShowDialog(false);
      setForm(emptyForm());
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/maintenance-windows/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['maintenance-windows'] }),
  });

  const openCreate = () => { setForm(emptyForm()); setShowDialog(true); };
  const openEdit = (w: MaintenanceWindow) => {
    setForm({
      id: w.id,
      name: w.name,
      description: w.description ?? '',
      isEnabled: w.isEnabled,
      mode: w.mode,
      scope: w.scopeKind,
      recurrence: w.recurrence,
      timeZoneId: w.timeZoneId,
      oneTimeStart: isoToLocalInput(w.oneTimeStartUtc),
      oneTimeEnd: isoToLocalInput(w.oneTimeEndUtc),
      daysMask: w.weeklyDaysMask,
      startTime: w.weeklyStartMinuteOfDay != null ? minuteToHhmm(w.weeklyStartMinuteOfDay) : '22:00',
      endTime: w.weeklyEndMinuteOfDay != null ? minuteToHhmm(w.weeklyEndMinuteOfDay) : '02:00',
      cronExpression: w.cronExpression ?? '',
      durationMinutes: w.durationMinutes != null ? String(w.durationMinutes) : '60',
      folderIds: w.targets.filter((tt) => tt.targetKind === 'Folder').map((tt) => tt.targetId),
      workflowIds: w.targets.filter((tt) => tt.targetKind === 'Workflow').map((tt) => tt.targetId),
    });
    setShowDialog(true);
  };

  const describeWhen = (w: MaintenanceWindow): string => {
    if (w.recurrence === 'OneTime')
      return `${w.oneTimeStartUtc ? new Date(w.oneTimeStartUtc).toLocaleString() : '?'} → ${w.oneTimeEndUtc ? new Date(w.oneTimeEndUtc).toLocaleString() : '?'}`;
    if (w.recurrence === 'Weekly')
      return `${daysFromMask(w.weeklyDaysMask, DAY_KEYS)} ${minuteToHhmm(w.weeklyStartMinuteOfDay)}–${minuteToHhmm(w.weeklyEndMinuteOfDay)} (${w.timeZoneId})`;
    if (w.recurrence === 'Cron')
      return `cron: ${w.cronExpression ?? '?'} (${w.durationMinutes ?? '?'} min, ${w.timeZoneId})`;
    return w.recurrence;
  };

  // Click-header-to-sort (same inline pattern as UsersPage/MachinesPage/GlobalVariablesPage).
  type SortKey = 'name' | 'enabled' | 'mode' | 'scope' | 'when' | 'targets';
  const [sortBy, setSortBy] = useState<SortKey>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const handleSort = (col: SortKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(col); setSortDir('asc'); }
  };
  const sortIcon = (col: SortKey) =>
    sortBy === col ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />) : <span className="w-3" />;

  const filteredSorted = useMemo(() => {
    const term = search.trim().toLowerCase();
    let rows = windows ?? [];
    if (term) rows = rows.filter((w) => w.name.toLowerCase().includes(term));
    return [...rows].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':    cmp = a.name.localeCompare(b.name); break;
        case 'enabled': cmp = Number(b.isEnabled) - Number(a.isEnabled); break;
        case 'mode':    cmp = a.mode.localeCompare(b.mode); break;
        case 'scope':   cmp = a.scopeKind.localeCompare(b.scopeKind); break;
        case 'when':    cmp = describeWhen(a).localeCompare(describeWhen(b)); break;
        case 'targets': {
          // "Global" shows "all workflows" — sort it after any concrete count on asc.
          const ta = a.scopeKind === 'Global' ? Infinity : a.targets.length;
          const tb = b.scopeKind === 'Global' ? Infinity : b.targets.length;
          cmp = ta - tb;
          break;
        }
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [windows, search, sortBy, sortDir]);

  // Resolve target ids to human names for the list. Dangling refs (deleted folder/workflow)
  // fall back to a placeholder rather than showing a raw guid.
  const folderPathById = useMemo(
    () => new Map((folders ?? []).map((f) => [f.id, f.path] as [string, string])), [folders]);
  const workflowNameById = useMemo(
    () => new Map((workflows ?? []).map((wf) => [wf.id, wf.name] as [string, string])), [workflows]);
  const targetNames = (w: MaintenanceWindow): string[] =>
    w.targets.map((tt) =>
      tt.targetKind === 'Folder'
        ? folderPathById.get(tt.targetId) ?? t('maintenance:unknownTarget')
        : workflowNameById.get(tt.targetId) ?? t('maintenance:unknownTarget'),
    );

  const toggleDay = (i: number) =>
    setForm((f) => ({ ...f, daysMask: f.daysMask ^ (1 << i) }));
  const toggleId = (key: 'folderIds' | 'workflowIds', id: string) =>
    setForm((f) => ({ ...f, [key]: f[key].includes(id) ? f[key].filter((x) => x !== id) : [...f[key], id] }));

  const formValid = useMemo(() => {
    if (!form.name.trim()) return false;
    if (form.scope === 'Folders' && form.folderIds.length === 0) return false;
    if (form.scope === 'Workflows' && form.workflowIds.length === 0) return false;
    if (form.recurrence === 'OneTime') {
      if (!form.oneTimeStart || !form.oneTimeEnd) return false;
      if (new Date(form.oneTimeEnd) <= new Date(form.oneTimeStart)) return false;
    }
    if (form.recurrence === 'Weekly') {
      if (form.daysMask === 0) return false;
      // start === end is valid and means a full 24h window for the selected days.
      const s = hhmmToMinute(form.startTime), e = hhmmToMinute(form.endTime);
      if (s == null || e == null) return false;
    }
    if (form.recurrence === 'Cron') {
      if (!form.cronExpression.trim()) return false;
      const d = Number(form.durationMinutes);
      if (!Number.isFinite(d) || d <= 0) return false;
    }
    return true;
  }, [form]);

  return (
    <div className="max-w-[1360px] mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          <p className="text-sm text-on-surface-variant mt-1 max-w-3xl">{t('maintenance:subtitle')}</p>
        </div>
        {canAdmin && (
          <button
            onClick={openCreate}
            title={t('maintenance:newWindow')}
            className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm"
          >
            <Add size={16} /> <span className="hidden sm:inline">{t('maintenance:newWindow')}</span>
          </button>
        )}
      </div>
      {(windows?.length ?? 0) > 0 && (
        <div className="np-card p-3 mb-3 flex items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('maintenance:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
        </div>
      )}
      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : !windows || windows.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('maintenance:empty')}</div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('maintenance:noMatch')}</div>
      ) : isMobile ? (
        <MobileCardList
          items={filteredSorted}
          getKey={(w) => w.id}
          renderTitle={(w) => (
            <div className="min-w-0">
              <div className="text-sm font-semibold text-on-surface truncate">{w.name}</div>
              {w.description && <div className="text-xs text-on-surface-variant truncate">{w.description}</div>}
            </div>
          )}
          renderFields={(w) => [
            {
              label: t('maintenance:tableHeaders.enabled'),
              value: <span className={`text-xs font-medium ${w.isEnabled ? 'text-green-600' : 'text-outline'}`}>{w.isEnabled ? t('common:yes') : t('common:no')}</span>,
            },
            {
              label: t('maintenance:tableHeaders.mode'),
              value: (
                <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium ${w.mode === 'Blackout' ? 'bg-red-500/15 text-red-600 dark:text-red-400' : 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400'}`}>
                  {w.mode === 'Blackout' ? <CalendarHeatMap size={11} /> : <CalendarSettings size={11} />}
                  {t(`maintenance:modes.${w.mode}`)}
                </span>
              ),
            },
            { label: t('maintenance:tableHeaders.scope'), value: <span className="text-sm text-on-surface-variant">{t(`maintenance:scopes.${w.scopeKind}`)}</span> },
            { label: t('maintenance:tableHeaders.when'), value: <span className="text-xs text-on-surface-variant font-mono break-words">{describeWhen(w)}</span> },
            {
              label: t('maintenance:tableHeaders.targetNames'),
              value: w.scopeKind === 'Global' ? (
                <span className="text-sm text-on-surface-variant">{t('maintenance:allWorkflows')}</span>
              ) : targetNames(w).length === 0 ? (
                <span className="text-on-surface-variant">{t('common:dash')}</span>
              ) : (
                <div className="flex flex-wrap gap-1">
                  {targetNames(w).map((n, i) => (
                    <span key={i} className="px-1.5 py-0.5 rounded text-[11px] font-mono bg-surface-container text-on-surface-variant truncate max-w-[220px]" title={n}>{n}</span>
                  ))}
                </div>
              ),
            },
          ]}
          renderActions={canAdmin ? (w) => (
            <>
              <button onClick={() => openEdit(w)} className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg" title={t('common:edit')}>
                <Edit size={16} />
              </button>
              <button
                onClick={async () => { if (await confirmDialog({ message: t('maintenance:deleteConfirm', { name: w.name }), danger: true })) deleteMutation.mutate(w.id); }}
                className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg"
                title={t('common:delete')}
              >
                <TrashCan size={16} />
              </button>
            </>
          ) : undefined}
        />
      ) : (
        <div className="np-card overflow-hidden"><div className="overflow-x-auto">
          <table className="w-full min-w-[1280px]">
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('name')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.name')}{sortIcon('name')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('enabled')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.enabled')}{sortIcon('enabled')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('mode')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.mode')}{sortIcon('mode')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('scope')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.scope')}{sortIcon('scope')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('when')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.when')}{sortIcon('when')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">
                  <button onClick={() => handleSort('targets')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('maintenance:tableHeaders.targets')}{sortIcon('targets')}
                  </button>
                </th>
                <th className="px-4 py-2 whitespace-nowrap">{t('maintenance:tableHeaders.targetNames')}</th>
                <th className="px-4 py-2 whitespace-nowrap">{t('maintenance:tableHeaders.actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/30">
              {filteredSorted.map((w) => (
                <tr key={w.id} className="hover:bg-surface-low">
                  <td className="px-4 py-2 overflow-hidden">
                    <div className="text-sm font-semibold text-on-surface-variant truncate">{w.name}</div>
                    {w.description && <div className="text-xs text-on-surface-variant truncate max-w-xs">{w.description}</div>}
                  </td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    <span className={`text-xs font-medium ${w.isEnabled ? 'text-green-600' : 'text-outline'}`}>
                      {w.isEnabled ? t('common:yes') : t('common:no')}
                    </span>
                  </td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    <span className={`inline-flex items-center gap-1 whitespace-nowrap px-2 py-0.5 rounded-full text-[11px] font-medium ${
                      w.mode === 'Blackout' ? 'bg-red-500/15 text-red-600 dark:text-red-400' : 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400'}`}>
                      {w.mode === 'Blackout' ? <CalendarHeatMap size={11} /> : <CalendarSettings size={11} />}
                      {t(`maintenance:modes.${w.mode}`)}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-sm text-on-surface-variant whitespace-nowrap">{t(`maintenance:scopes.${w.scopeKind}`)}</td>
                  <td className="px-4 py-2 text-xs text-on-surface-variant font-mono whitespace-nowrap">{describeWhen(w)}</td>
                  <td className="px-4 py-2 text-sm text-on-surface-variant whitespace-nowrap">
                    {w.scopeKind === 'Global' ? t('maintenance:all') : w.targets.length}
                  </td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    {w.scopeKind === 'Global' ? (
                      <span className="text-sm text-on-surface-variant whitespace-nowrap">{t('maintenance:allWorkflows')}</span>
                    ) : targetNames(w).length === 0 ? (
                      <span className="text-on-surface-variant">{t('common:dash')}</span>
                    ) : (
                      <div className="flex flex-nowrap gap-1">
                        {targetNames(w).map((n, i) => (
                          <span
                            key={i}
                            className="px-1.5 py-0.5 rounded text-[11px] font-mono bg-surface-container text-on-surface-variant truncate max-w-[220px] whitespace-nowrap"
                            title={n}
                          >
                            {n}
                          </span>
                        ))}
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    {canAdmin && (
                      <div className="flex items-center gap-1 whitespace-nowrap">
                        <button onClick={() => openEdit(w)} className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg" title={t('common:edit')}>
                          <Edit size={16} />
                        </button>
                        <button
                          onClick={async () => { if (await confirmDialog({ message: t('maintenance:deleteConfirm', { name: w.name }), danger: true })) deleteMutation.mutate(w.id); }}
                          className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg"
                          title={t('common:delete')}
                        >
                          <TrashCan size={16} />
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div></div>
      )}
      {showDialog && (
        <ModalShell onClose={() => setShowDialog(false)} maxWidth="max-w-lg">
            <h3 className="text-lg font-semibold mb-4 text-on-surface">
              {form.id ? t('maintenance:editTitle') : t('maintenance:createTitle')}
            </h3>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.name')}</label>
                <input
                  type="text"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.description')}</label>
                <input
                  type="text"
                  value={form.description}
                  onChange={(e) => setForm({ ...form, description: e.target.value })}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>

              <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
                <input type="checkbox" checked={form.isEnabled} onChange={(e) => setForm({ ...form, isEnabled: e.target.checked })} className="rounded" />
                {t('maintenance:fields.enabled')}
              </label>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.mode')}</label>
                  <select value={form.mode} onChange={(e) => setForm({ ...form, mode: e.target.value as Mode })}
                    className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
                    <option value="Blackout">{t('maintenance:modes.Blackout')}</option>
                    <option value="AllowOnly">{t('maintenance:modes.AllowOnly')}</option>
                  </select>
                </div>
                <div>
                  <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.recurrence')}</label>
                  <select value={form.recurrence} onChange={(e) => setForm({ ...form, recurrence: e.target.value as Recurrence })}
                    className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
                    <option value="Weekly">{t('maintenance:recurrences.Weekly')}</option>
                    <option value="OneTime">{t('maintenance:recurrences.OneTime')}</option>
                    <option value="Cron">{t('maintenance:recurrences.Cron')}</option>
                  </select>
                </div>
              </div>

              {form.recurrence === 'Weekly' && (
                <>
                  <div>
                    <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.days')}</label>
                    <div className="flex flex-wrap gap-1">
                      {DAY_KEYS.map((d, i) => (
                        <button
                          key={d}
                          type="button"
                          onClick={() => toggleDay(i)}
                          className={`px-2 py-1 rounded text-xs font-medium border ${
                            (form.daysMask & (1 << i)) !== 0
                              ? 'bg-blue-600 text-white border-blue-600'
                              : 'border-outline-variant text-on-surface-variant hover:bg-surface-container'}`}
                        >
                          {d}
                        </button>
                      ))}
                    </div>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.startTime')}</label>
                      <input type="time" value={form.startTime} onChange={(e) => setForm({ ...form, startTime: e.target.value })}
                        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                    </div>
                    <div>
                      <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.endTime')}</label>
                      <input type="time" value={form.endTime} onChange={(e) => setForm({ ...form, endTime: e.target.value })}
                        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                    </div>
                  </div>
                  <p className="text-[11px] text-outline">{t('maintenance:wrapHint')}</p>
                  <div>
                    <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.timezone')}</label>
                    <input type="text" value={form.timeZoneId} onChange={(e) => setForm({ ...form, timeZoneId: e.target.value })}
                      className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono" />
                  </div>
                </>
              )}

              {form.recurrence === 'Cron' && (
                <>
                  <div>
                    <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.cronExpression')}</label>
                    <input type="text" value={form.cronExpression} onChange={(e) => setForm({ ...form, cronExpression: e.target.value })}
                      placeholder="0 0 3 ? * SAT"
                      className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono" />
                  </div>
                  <p className="text-[11px] text-outline">{t('maintenance:cronHint')}</p>
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.durationMinutes')}</label>
                      <input type="number" min={1} value={form.durationMinutes} onChange={(e) => setForm({ ...form, durationMinutes: e.target.value })}
                        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                    </div>
                    <div>
                      <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.timezone')}</label>
                      <input type="text" value={form.timeZoneId} onChange={(e) => setForm({ ...form, timeZoneId: e.target.value })}
                        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono" />
                    </div>
                  </div>
                </>
              )}

              {form.recurrence === 'OneTime' && (
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.oneTimeStart')}</label>
                    <input type="datetime-local" value={form.oneTimeStart} onChange={(e) => setForm({ ...form, oneTimeStart: e.target.value })}
                      className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.oneTimeEnd')}</label>
                    <input type="datetime-local" value={form.oneTimeEnd} onChange={(e) => setForm({ ...form, oneTimeEnd: e.target.value })}
                      className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                  </div>
                </div>
              )}

              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.scope')}</label>
                <select value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value as Scope })}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
                  <option value="Global">{t('maintenance:scopes.Global')}</option>
                  <option value="Folders">{t('maintenance:scopes.Folders')}</option>
                  <option value="Workflows">{t('maintenance:scopes.Workflows')}</option>
                </select>
              </div>

              {form.scope === 'Folders' && (
                <div>
                  <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.folders')}</label>
                  <div className="max-h-40 overflow-y-auto border border-outline-variant rounded-md p-2 space-y-1">
                    {(folders ?? []).map((fo) => (
                      <label key={fo.id} className="flex items-center gap-2 text-sm cursor-pointer">
                        <input type="checkbox" checked={form.folderIds.includes(fo.id)} onChange={() => toggleId('folderIds', fo.id)} className="rounded" />
                        <span className="font-mono text-xs">{fo.path}</span>
                      </label>
                    ))}
                  </div>
                </div>
              )}

              {form.scope === 'Workflows' && (
                <div>
                  <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('maintenance:fields.workflows')}</label>
                  <div className="max-h-40 overflow-y-auto border border-outline-variant rounded-md p-2 space-y-1">
                    {(workflows ?? []).map((wf) => (
                      <label key={wf.id} className="flex items-center gap-2 text-sm cursor-pointer">
                        <input type="checkbox" checked={form.workflowIds.includes(wf.id)} onChange={() => toggleId('workflowIds', wf.id)} className="rounded" />
                        <span className="truncate">{wf.name}</span>
                      </label>
                    ))}
                  </div>
                </div>
              )}
            </div>

            <div className="flex justify-end gap-2 mt-5">
              <button onClick={() => { setShowDialog(false); setForm(emptyForm()); }}
                className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md">
                {t('common:cancel')}
              </button>
              <button onClick={() => saveMutation.mutate(form)} disabled={!formValid || saveMutation.isPending}
                className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50">
                {saveMutation.isPending ? t('common:saving') : (form.id ? t('common:update') : t('common:create'))}
              </button>
            </div>
        </ModalShell>
      )}
    </div>
  );
}
