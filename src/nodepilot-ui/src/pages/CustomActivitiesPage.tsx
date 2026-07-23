import {
  Add,
  ChevronDown,
  ChevronUp,
  Close,
  Download,
  Edit,
  History,
  Power,
  Reset,
  Search,
  TrashCan,
  Upload,
} from '@carbon/icons-react';
import { ACTIVITY_ICON_COMPONENTS, FALLBACK_ACTIVITY_ICON, CUSTOM_ACTIVITY_ICON_CHOICES } from '../lib/activityIcons';
import { useMemo, useRef, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import CodeMirror from '@uiw/react-codemirror';
import { StreamLanguage } from '@codemirror/language';
import { powerShell } from '@codemirror/legacy-modes/mode/powershell';
import { EditorView } from '@codemirror/view';
import { api } from '../api/client';
import { ModalShell } from '../components/common/ModalShell';
import { useRole } from '../lib/rbac';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import { useThemeStore, resolveTheme } from '../stores/themeStore';

/**
 * Admin/Operator management surface for custom activities ("Custom Nodes"). A definition is created
 * disabled (Draft); Admin+Operator may edit/delete while disabled, but once an Admin enables it only
 * an Admin can modify it (the server enforces this — the UI mirrors it). Secrets must come via
 * {{globals.X}}, never as inputs (there is no secret input type).
 */

interface InputParam {
  name: string;
  label: string;
  type: 'string' | 'number' | 'boolean' | 'select' | 'multiline';
  required?: boolean;
  default?: string | null;
  options?: string[] | null;
  description?: string | null;
}
interface OutputParam { name: string; type: 'string' | 'number' | 'boolean' | 'object' | 'array' }

interface CatalogEntry {
  id: string; key: string; type: string; name: string; description: string | null;
  icon: string; color: string | null; runsRemote: boolean; timeout: string;
  inputs: InputParam[]; outputs: OutputParam[]; isEnabled: boolean; version: number;
}
interface FullDef extends CatalogEntry {
  scriptTemplate: string; engine: string; isolated: boolean;
  memoryLimitMb: number | null; maxProcesses: number | null;
  defaultTimeoutSeconds: number | null; successExitCodes: string | null;
  concurrencyToken: string; updatedAt: string; updatedBy: string | null;
}
/** One historical snapshot from GET /custom-activities/{id}/versions (only the fields the modal shows). */
interface VersionEntry {
  version: number; name: string; description: string | null;
  engine: string; runsRemote: boolean;
  createdAt: string; createdBy: string | null; changeNote: string | null;
}

interface FormState {
  id: string | null;
  key: string; name: string; description: string; icon: string; color: string;
  scriptTemplate: string; engine: string; runsRemote: boolean; isolated: boolean;
  defaultTimeoutSeconds: string; successExitCodes: string;
  inputs: InputParam[]; outputs: OutputParam[];
  concurrencyToken: string | null; isEnabled: boolean;
}

const EMPTY: FormState = {
  id: null, key: '', name: '', description: '', icon: 'extension', color: '',
  scriptTemplate: '', engine: 'auto', runsRemote: false, isolated: false,
  defaultTimeoutSeconds: '', successExitCodes: '', inputs: [], outputs: [],
  concurrencyToken: null, isEnabled: false,
};

// Curated icon palette for the picker; free-text entry is also allowed for any Material Symbol.
/** Renders a custom-activity icon token as its Carbon component (fallback for unknown tokens). */
function IconGlyph({ token, size, className, color }: Readonly<{ token?: string; size: number; className?: string; color?: string }>) {
  const Icon = ACTIVITY_ICON_COMPONENTS[token ?? ''] ?? FALLBACK_ACTIVITY_ICON;
  return <Icon size={size} className={className} style={color ? { color } : undefined} />;
}

export function CustomActivitiesPage() {
  const { t } = useTranslation(['customActivities', 'common']);
  const qc = useQueryClient();
  const { canWrite, canAdmin } = useRole();
  const [form, setForm] = useState<FormState>(EMPTY);
  const [showDialog, setShowDialog] = useState(false);
  const [showIconPicker, setShowIconPicker] = useState(false);
  const [iconSearch, setIconSearch] = useState('');
  const [warnings, setWarnings] = useState<{ rule: string; message: string }[]>([]);
  const [search, setSearch] = useState('');
  const [versionsForId, setVersionsForId] = useState<string | null>(null);
  const importInputRef = useRef<HTMLInputElement>(null);

  const { data: list, isLoading } = useQuery({
    queryKey: ['custom-activities', 'list'],
    queryFn: () => api.get<CatalogEntry[]>('/custom-activities?includeDisabled=true'),
  });

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['custom-activities', 'list'] });
    qc.invalidateQueries({ queryKey: ['custom-activities', 'catalog'] }); // refresh the designer palette cache
  };

  const saveMutation = useMutation({
    mutationFn: async (f: FormState) => {
      const body = {
        name: f.name.trim(), description: f.description.trim() || null, icon: f.icon.trim() || 'extension',
        color: f.color.trim() || null, scriptTemplate: f.scriptTemplate, engine: f.engine,
        runsRemote: f.runsRemote, isolated: f.isolated,
        defaultTimeoutSeconds: f.defaultTimeoutSeconds ? Number(f.defaultTimeoutSeconds) : null,
        successExitCodes: f.successExitCodes.trim() || null,
        inputs: f.inputs, outputs: f.outputs,
      };
      if (f.id) {
        return api.put<{ definition: FullDef; warnings: { rule: string; message: string }[] }>(
          `/custom-activities/${f.id}`,
          { ...body, concurrencyToken: f.concurrencyToken, changeNote: null });
      }
      return api.post<{ definition: FullDef; warnings: { rule: string; message: string }[] }>(
        '/custom-activities', { ...body, key: f.key.trim() });
    },
    onSuccess: (res) => {
      invalidate();
      setWarnings(res.warnings ?? []);
      if (!res.warnings || res.warnings.length === 0) { setShowDialog(false); setForm(EMPTY); }
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/custom-activities/${id}`),
    onSuccess: invalidate,
    onError: (err: Error) => toast.error(t('common:deleteFailed', { message: err.message })),
  });
  const enableMutation = useMutation({
    mutationFn: ({ id, enable }: { id: string; enable: boolean }) =>
      api.post(`/custom-activities/${id}/${enable ? 'enable' : 'disable'}`, {}),
    onSuccess: invalidate,
    onError: (err: Error) => toast.error(err.message),
  });

  const openCreate = () => { setForm(EMPTY); setWarnings([]); setShowDialog(true); };
  const openEdit = async (id: string) => {
    const d = await api.get<FullDef>(`/custom-activities/${id}`);
    setForm({
      id: d.id, key: d.key, name: d.name, description: d.description ?? '', icon: d.icon, color: d.color ?? '',
      scriptTemplate: d.scriptTemplate, engine: d.engine, runsRemote: d.runsRemote, isolated: d.isolated,
      defaultTimeoutSeconds: d.defaultTimeoutSeconds?.toString() ?? '', successExitCodes: d.successExitCodes ?? '',
      inputs: d.inputs ?? [], outputs: d.outputs ?? [], concurrencyToken: d.concurrencyToken, isEnabled: d.isEnabled,
    });
    setWarnings([]);
    setShowDialog(true);
  };

  const onExport = async () => {
    const env = await api.get('/custom-activities/export');
    const blob = new Blob([JSON.stringify(env, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = 'custom-nodes.npca.json'; a.click();
    URL.revokeObjectURL(url);
  };
  // File-picker import (same pattern as WorkflowsPage): hidden <input type="file">,
  // read the .npca/.json envelope client-side, POST it as-is. Imported nodes land
  // disabled server-side and need an Admin review + enable.
  const importMutation = useMutation({
    mutationFn: async (file: File) => {
      const text = await file.text();
      let envelope: unknown;
      try { envelope = JSON.parse(text); } catch {
        throw new Error(t('customActivities:importInvalidJson', { file: file.name }));
      }
      return api.post<unknown[]>('/custom-activities/import', envelope);
    },
    onSuccess: (imported) => {
      invalidate();
      toast.success(t('customActivities:importDone', { count: imported.length }));
    },
    onError: (err: Error) => toast.error(err.message),
  });
  const handleImportFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) importMutation.mutate(file);
    // Always reset so re-selecting the same file fires onChange again.
    e.target.value = '';
  };

  // A live (enabled) node is editable only by Admin (mirrors the server gate).
  const canEdit = (e: CatalogEntry) => canWrite && (canAdmin || !e.isEnabled);

  // Click-header-to-sort (same inline pattern as UsersPage/MachinesPage/GlobalVariablesPage).
  type SortKey = 'name' | 'key' | 'status' | 'scope' | 'version';
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
    let rows = list ?? [];
    if (term) rows = rows.filter((r) => r.name.toLowerCase().includes(term) || r.key.toLowerCase().includes(term));
    return [...rows].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':    cmp = a.name.localeCompare(b.name); break;
        case 'key':     cmp = a.key.localeCompare(b.key); break;
        case 'status':  cmp = Number(b.isEnabled) - Number(a.isEnabled); break;
        case 'scope':   cmp = Number(b.runsRemote) - Number(a.runsRemote); break;
        case 'version': cmp = a.version - b.version; break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [list, search, sortBy, sortDir]);

  return (
    <div className="max-w-4xl mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          <p className="text-sm text-on-surface-variant mt-1">{t('customActivities:subtitle')}</p>
        </div>
        {canWrite && (
          <div className="flex items-center gap-2">
            <input
              ref={importInputRef}
              type="file"
              accept=".npca,.json,application/json"
              className="hidden"
              onChange={handleImportFile}
            />
            <button onClick={() => importInputRef.current?.click()} disabled={importMutation.isPending}
              className="flex items-center gap-2 px-3 py-2 bg-surface-container text-on-surface rounded-md hover:bg-surface-high text-sm whitespace-nowrap disabled:opacity-50">
              <Upload size={16} /> <span className="hidden sm:inline">{importMutation.isPending ? t('common:importing') : t('customActivities:import')}</span>
            </button>
            <button onClick={onExport} className="flex items-center gap-2 px-3 py-2 bg-surface-container text-on-surface rounded-md hover:bg-surface-high text-sm whitespace-nowrap">
              <Download size={16} /> <span className="hidden sm:inline">{t('customActivities:export')}</span>
            </button>
            <button onClick={openCreate} className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm whitespace-nowrap">
              <Add size={16} /> <span className="hidden sm:inline">{t('customActivities:new')}</span>
            </button>
          </div>
        )}
      </div>
      {(list?.length ?? 0) > 0 && (
        <div className="np-card p-3 mb-3">
          <div className="relative w-full sm:max-w-sm">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder={t('customActivities:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
          </div>
        </div>
      )}
      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : (list?.length ?? 0) === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('customActivities:empty')}</div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('customActivities:noMatch')}</div>
      ) : (
        <div className="np-card overflow-hidden"><div className="overflow-x-auto">
          <table className="w-full">
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                <th className="px-4 py-2">
                  <button onClick={() => handleSort('name')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('customActivities:columns.name')}{sortIcon('name')}
                  </button>
                </th>
                <th className="px-4 py-2">
                  <button onClick={() => handleSort('key')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('customActivities:columns.key')}{sortIcon('key')}
                  </button>
                </th>
                <th className="px-4 py-2">
                  <button onClick={() => handleSort('status')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('customActivities:columns.status')}{sortIcon('status')}
                  </button>
                </th>
                <th className="px-4 py-2">
                  <button onClick={() => handleSort('scope')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('customActivities:columns.scope')}{sortIcon('scope')}
                  </button>
                </th>
                <th className="px-4 py-2">
                  <button onClick={() => handleSort('version')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('customActivities:columns.version')}{sortIcon('version')}
                  </button>
                </th>
                <th className="px-4 py-2">{t('customActivities:columns.actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/30">
              {filteredSorted.map((e) => (
                <tr key={e.id} className="hover:bg-surface-low">
                  <td className="px-4 py-2">
                    <div className="flex items-center gap-2">
                      <IconGlyph token={e.icon} size={18} className="text-indigo-500" color={e.color ?? undefined} />
                      <span className="text-sm font-medium text-on-surface">{e.name}</span>
                    </div>
                  </td>
                  <td className="px-4 py-2"><code className="text-xs font-mono text-on-surface-variant">{e.key}</code></td>
                  <td className="px-4 py-2">
                    {e.isEnabled ? (
                      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-green-500/15 text-green-600 dark:text-green-400">{t('customActivities:status.enabled')}</span>
                    ) : (
                      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium bg-amber-500/15 text-amber-600 dark:text-amber-400">{t('customActivities:status.draft')}</span>
                    )}
                  </td>
                  <td className="px-4 py-2 text-xs text-on-surface-variant">{e.runsRemote ? t('customActivities:scope.remote') : t('customActivities:scope.local')}</td>
                  <td className="px-4 py-2 text-xs text-on-surface-variant tabular-nums">v{e.version}</td>
                  <td className="px-4 py-2">
                    <div className="flex items-center gap-1">
                      <button onClick={() => openEdit(e.id)} disabled={!canEdit(e)} title={canEdit(e) ? t('customActivities:actions.edit') : t('customActivities:lockedHint')}
                        className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg disabled:opacity-40 disabled:cursor-not-allowed"><Edit size={16} /></button>
                      {canAdmin && (
                        <button onClick={() => enableMutation.mutate({ id: e.id, enable: !e.isEnabled })}
                          title={e.isEnabled ? t('customActivities:actions.disable') : t('customActivities:actions.enable')}
                          className="p-1.5 text-on-surface-variant hover:bg-surface-container rounded-lg">
                          {e.isEnabled ? <Power size={16} /> : <Power size={16} />}
                        </button>
                      )}
                      {canWrite && (
                        <button onClick={() => setVersionsForId(e.id)} title={t('customActivities:actions.versions')}
                          className="p-1.5 text-on-surface-variant hover:bg-surface-container rounded-lg"><History size={16} /></button>
                      )}
                      <button onClick={async () => { if (canEdit(e) && (await confirmDialog({ message: t('customActivities:deleteConfirm', { name: e.name }), danger: true }))) deleteMutation.mutate(e.id); }}
                        disabled={!canEdit(e)} title={t('customActivities:actions.delete')}
                        className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg disabled:opacity-40 disabled:cursor-not-allowed"><TrashCan size={16} /></button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div></div>
      )}
      {showDialog && (
        <ModalShell onClose={() => setShowDialog(false)} maxWidth="max-w-3xl">
          <h3 className="text-lg font-semibold mb-4 text-on-surface">{form.id ? t('customActivities:editTitle') : t('customActivities:create')}</h3>
          <div className="space-y-3 max-h-[70vh] overflow-y-auto pr-1">
            <div className="grid grid-cols-2 gap-3">
              <Labeled label={t('customActivities:fields.name')}>
                <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className="input-field" />
              </Labeled>
              <Labeled label={t('customActivities:fields.key')} hint={!form.id ? t('customActivities:fields.keyHint') : undefined}>
                <input value={form.key} disabled={!!form.id} onChange={(e) => setForm({ ...form, key: e.target.value })}
                  placeholder="disk-check" pattern="[A-Za-z0-9_\-]+" className="input-field font-mono disabled:opacity-60" />
              </Labeled>
            </div>

            <div className="grid grid-cols-[auto_1fr_auto] gap-3 items-end">
              <Labeled label={t('customActivities:fields.icon')}>
                <button type="button" onClick={() => setShowIconPicker(true)} className="flex items-center gap-2 input-field">
                  <IconGlyph token={form.icon} size={20} color={form.color || undefined} />
                  <span className="text-xs font-mono text-on-surface-variant">{form.icon}</span>
                </button>
              </Labeled>
              <Labeled label={t('customActivities:fields.description')}>
                <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} className="input-field" />
              </Labeled>
              <Labeled label={t('customActivities:fields.color')}>
                <input type="color" value={form.color || '#6366f1'} onChange={(e) => setForm({ ...form, color: e.target.value })} className="h-9 w-12 rounded border border-outline-variant" />
              </Labeled>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <Labeled label={t('customActivities:fields.engine')}>
                <select value={form.engine} onChange={(e) => setForm({ ...form, engine: e.target.value })} className="input-field">
                  <option value="auto">Auto (PS7 → PS5.1)</option>
                  <option value="pwsh">PowerShell 7</option>
                  <option value="powershell">Windows PS 5.1</option>
                </select>
              </Labeled>
              <Labeled label={t('customActivities:fields.defaultTimeout')}>
                <input type="number" min={0} value={form.defaultTimeoutSeconds} onChange={(e) => setForm({ ...form, defaultTimeoutSeconds: e.target.value })} className="input-field" />
              </Labeled>
            </div>

            <div className="flex flex-wrap gap-4">
              <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
                <input type="checkbox" checked={form.runsRemote} onChange={(e) => setForm({ ...form, runsRemote: e.target.checked })} /> {t('customActivities:fields.runsRemote')}
              </label>
              <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
                <input type="checkbox" checked={form.isolated} onChange={(e) => setForm({ ...form, isolated: e.target.checked })} /> {t('customActivities:fields.isolated')}
              </label>
              <Labeled label={t('customActivities:fields.successExitCodes')}>
                <input value={form.successExitCodes} onChange={(e) => setForm({ ...form, successExitCodes: e.target.value })} placeholder="0,1" className="input-field w-28 font-mono" />
              </Labeled>
            </div>

            <Labeled label={t('customActivities:fields.script')} hint={t('customActivities:fields.scriptHint')}>
              <ScriptTemplateEditor value={form.scriptTemplate}
                onChange={(v) => setForm((f) => ({ ...f, scriptTemplate: v }))} />
            </Labeled>

            <ParamEditor kind="input" params={form.inputs} onChange={(p) => setForm({ ...form, inputs: p as InputParam[] })} t={t} />
            <ParamEditor kind="output" params={form.outputs} onChange={(p) => setForm({ ...form, outputs: p as OutputParam[] })} t={t} />

            {warnings.length > 0 && (
              <div className="rounded-md border border-amber-500/40 bg-amber-500/10 p-3">
                <p className="text-xs font-semibold text-amber-700 dark:text-amber-400 mb-1">{t('customActivities:lintWarnings')}</p>
                <ul className="list-disc list-inside text-xs text-amber-700 dark:text-amber-300 space-y-0.5">
                  {warnings.map((w, i) => <li key={i}>{w.message}</li>)}
                </ul>
              </div>
            )}
          </div>

          <div className="flex justify-end gap-2 mt-5">
            <button onClick={() => { setShowDialog(false); setForm(EMPTY); }} className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md">{t('common:cancel')}</button>
            <button onClick={() => saveMutation.mutate(form)} disabled={!form.name.trim() || (!form.id && !form.key.trim()) || !form.scriptTemplate.trim() || saveMutation.isPending}
              className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50">
              {saveMutation.isPending ? t('common:saving') : (form.id ? t('common:update') : t('common:create'))}
            </button>
          </div>
        </ModalShell>
      )}
      {versionsForId && (() => {
        // Resolve the entry fresh from the query cache so the "current version" label
        // updates when a rollback bumps the live version while the modal stays open.
        const entry = (list ?? []).find((x) => x.id === versionsForId);
        return entry ? (
          <VersionsModal entry={entry} canRollback={canEdit(entry)} onClose={() => setVersionsForId(null)} />
        ) : null;
      })()}
      {showIconPicker && (
        <ModalShell onClose={() => setShowIconPicker(false)} maxWidth="max-w-lg">
          <div className="flex items-center justify-between mb-3">
            <h3 className="text-base font-semibold text-on-surface">{t('customActivities:iconPicker.title')}</h3>
            <button onClick={() => setShowIconPicker(false)} className="p-1 text-on-surface-variant hover:bg-surface-container rounded"><Close size={16} /></button>
          </div>
          <input value={iconSearch} onChange={(e) => setIconSearch(e.target.value)} placeholder={t('customActivities:iconPicker.search')}
            className="input-field mb-3 font-mono" />
          <div className="grid grid-cols-8 gap-1 max-h-[40vh] overflow-y-auto">
            {CUSTOM_ACTIVITY_ICON_CHOICES.filter((ic) => ic.includes(iconSearch.trim().toLowerCase())).map((ic) => (
              <button key={ic} onClick={() => { setForm({ ...form, icon: ic }); setShowIconPicker(false); }}
                title={ic} className={`flex items-center justify-center h-10 rounded hover:bg-surface-container ${form.icon === ic ? 'bg-blue-500/15 ring-1 ring-blue-500' : ''}`}>
                <IconGlyph token={ic} size={22} />
              </button>
            ))}
          </div>
        </ModalShell>
      )}
    </div>
  );
}

const POWERSHELL_LANGUAGE = StreamLanguage.define(powerShell);

// Compact CodeMirror theme reading the app's CSS variables — trimmed-down sibling of
// panelChrome's compactEditorTheme (which is coupled to designer upstream-vars, so we
// keep a small local editor here instead of reusing CodeField).
const SCRIPT_EDITOR_THEME = EditorView.theme({
  '&': { backgroundColor: 'var(--color-surface-lowest)', color: 'var(--color-on-surface)', fontSize: '12px' },
  '.cm-content': { padding: '6px 0' },
  '.cm-gutters': { backgroundColor: 'var(--color-surface-low)', color: 'var(--color-outline)', borderRight: '1px solid var(--color-outline-variant)', fontSize: '12px' },
  '.cm-cursor': { borderLeftColor: 'var(--color-primary)' },
});
const SCRIPT_EDITOR_EXTENSIONS = [POWERSHELL_LANGUAGE, EditorView.lineWrapping, SCRIPT_EDITOR_THEME];

/** PowerShell-highlighted CodeMirror editor for the script template, sized like the old 10-row textarea. */
function ScriptTemplateEditor({ value, onChange }: Readonly<{ value: string; onChange: (v: string) => void }>) {
  const theme = useThemeStore((s) => s.theme);
  const isDark = resolveTheme(theme) === 'dark';
  return (
    <div className="border border-outline-variant rounded-md overflow-hidden bg-surface-lowest"
      style={{ resize: 'vertical', minHeight: 180, maxHeight: 600, overflow: 'auto' }}>
      <CodeMirror
        value={value}
        onChange={onChange}
        theme={isDark ? 'dark' : 'light'}
        extensions={SCRIPT_EDITOR_EXTENSIONS}
        basicSetup={{
          lineNumbers: true,
          foldGutter: false,
          highlightActiveLine: false,
          highlightActiveLineGutter: false,
        }}
      />
    </div>
  );
}

/**
 * Version-history dialog: lists the stored snapshots (newest first) with a per-row rollback.
 * The snapshot table only holds PREVIOUS versions — the live definition (entry.version) is
 * shown in the header, so every listed row is a valid rollback target. Rollback itself
 * creates a new version (the current state is snapshotted first, server-side).
 */
function VersionsModal({ entry, canRollback, onClose }: Readonly<{
  entry: CatalogEntry;
  canRollback: boolean;
  onClose: () => void;
}>) {
  const { t } = useTranslation(['customActivities', 'common']);
  const qc = useQueryClient();
  const { data: versions, isLoading } = useQuery({
    queryKey: ['custom-activities', 'versions', entry.id],
    queryFn: () => api.get<VersionEntry[]>(`/custom-activities/${entry.id}/versions`),
  });
  const rollbackMutation = useMutation({
    mutationFn: (version: number) => api.post(`/custom-activities/${entry.id}/rollback/${version}`, {}),
    onSuccess: (_res, version) => {
      // Prefix-invalidation refreshes the page list, the designer palette catalog AND
      // this modal's versions query (the rollback snapshot appears as a new row).
      qc.invalidateQueries({ queryKey: ['custom-activities'] });
      toast.success(t('customActivities:rollbackDone', { version }));
    },
    onError: (err: Error) => toast.error(err.message),
  });

  const onRollback = async (version: number) => {
    if (await confirmDialog({ message: t('customActivities:rollbackConfirm', { name: entry.name, version }), danger: true })) {
      rollbackMutation.mutate(version);
    }
  };

  return (
    <ModalShell onClose={onClose} maxWidth="max-w-2xl">
      <div className="flex items-center justify-between mb-1">
        <h3 className="text-lg font-semibold text-on-surface">{t('customActivities:versionsTitle')} — {entry.name}</h3>
        <button onClick={onClose} className="p-1 text-on-surface-variant hover:bg-surface-container rounded"><Close size={16} /></button>
      </div>
      <p className="text-xs text-on-surface-variant mb-3">{t('customActivities:versions.current', { version: entry.version })}</p>
      {isLoading ? (
        <p className="text-outline text-sm">{t('common:loadingDots')}</p>
      ) : (versions?.length ?? 0) === 0 ? (
        <p className="text-sm text-outline py-4 text-center">{t('customActivities:versions.empty')}</p>
      ) : (
        <div className="overflow-x-auto max-h-[55vh] overflow-y-auto">
          <table className="w-full">
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                <th className="px-3 py-2">{t('customActivities:versions.version')}</th>
                <th className="px-3 py-2">{t('customActivities:versions.createdAt')}</th>
                <th className="px-3 py-2">{t('customActivities:versions.createdBy')}</th>
                <th className="px-3 py-2">{t('customActivities:versions.changeNote')}</th>
                <th className="px-3 py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/30">
              {(versions ?? []).map((v) => (
                <tr key={v.version} className="hover:bg-surface-low">
                  <td className="px-3 py-2 text-sm font-medium text-on-surface tabular-nums">v{v.version}</td>
                  <td className="px-3 py-2 text-xs text-on-surface-variant whitespace-nowrap">{new Date(v.createdAt).toLocaleString()}</td>
                  <td className="px-3 py-2 text-xs text-on-surface-variant">{v.createdBy ?? '—'}</td>
                  <td className="px-3 py-2 text-xs text-on-surface-variant">{v.changeNote ?? '—'}</td>
                  <td className="px-3 py-2 text-right">
                    {canRollback && (
                      <button onClick={() => onRollback(v.version)} disabled={rollbackMutation.isPending}
                        title={t('customActivities:rollback')}
                        className="inline-flex items-center gap-1.5 px-2 py-1 text-xs text-blue-600 hover:bg-blue-500/10 rounded-md disabled:opacity-50">
                        <Reset size={13} /> {t('customActivities:rollback')}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </ModalShell>
  );
}

function Labeled({ label, hint, children }: Readonly<{ label: string; hint?: string; children: React.ReactNode }>) {
  return (
    <div className="space-y-1">
      <label className="block text-xs font-medium text-on-surface-variant">{label}</label>
      {children}
      {hint && <p className="text-[11px] text-outline">{hint}</p>}
    </div>
  );
}

function ParamEditor({ kind, params, onChange, t }: Readonly<{
  kind: 'input' | 'output';
  params: (InputParam | OutputParam)[];
  onChange: (p: (InputParam | OutputParam)[]) => void;
  t: (k: string) => string;
}>) {
  const isInput = kind === 'input';
  const inputTypes = ['string', 'number', 'boolean', 'select', 'multiline'];
  const outputTypes = ['string', 'number', 'boolean', 'object', 'array'];
  const add = () => onChange([...params, isInput
    ? { name: '', label: '', type: 'string' } as InputParam
    : { name: '', type: 'string' } as OutputParam]);
  const update = (i: number, patch: Record<string, unknown>) =>
    onChange(params.map((p, idx) => (idx === i ? { ...p, ...patch } : p)));
  const remove = (i: number) => onChange(params.filter((_, idx) => idx !== i));

  return (
    <div className="rounded-md border border-outline-variant/40 p-2">
      <div className="flex items-center justify-between mb-2">
        <span className="text-xs font-semibold text-on-surface-variant">{isInput ? t('customActivities:fields.inputs') : t('customActivities:fields.outputs')}</span>
        <button onClick={add} className="text-xs text-blue-600 hover:underline flex items-center gap-1"><Add size={12} />{isInput ? t('customActivities:param.addInput') : t('customActivities:param.addOutput')}</button>
      </div>
      <div className="space-y-1">
        {params.map((p, i) => (
          <div key={i} className="flex flex-wrap items-center gap-1">
            <input value={p.name} onChange={(e) => update(i, { name: e.target.value })} placeholder={t('customActivities:param.name')} className="input-field w-28 font-mono text-xs" />
            {isInput && <input value={(p as InputParam).label} onChange={(e) => update(i, { label: e.target.value })} placeholder={t('customActivities:param.label')} className="input-field w-32 text-xs" />}
            <select value={p.type} onChange={(e) => update(i, { type: e.target.value })} className="input-field w-28 text-xs">
              {(isInput ? inputTypes : outputTypes).map((ty) => <option key={ty} value={ty}>{ty}</option>)}
            </select>
            {isInput && (
              <label className="flex items-center gap-1 text-[11px] text-on-surface-variant">
                <input type="checkbox" checked={!!(p as InputParam).required} onChange={(e) => update(i, { required: e.target.checked })} />{t('customActivities:param.required')}
              </label>
            )}
            {isInput && (p as InputParam).type === 'select' && (
              <input value={((p as InputParam).options ?? []).join(',')} onChange={(e) => update(i, { options: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })}
                placeholder={t('customActivities:param.options')} className="input-field flex-1 min-w-[140px] text-xs" />
            )}
            <button onClick={() => remove(i)} className="p-1 text-red-600 hover:bg-red-500/15 rounded" title={t('customActivities:param.remove')}><TrashCan size={14} /></button>
          </div>
        ))}
      </div>
    </div>
  );
}
