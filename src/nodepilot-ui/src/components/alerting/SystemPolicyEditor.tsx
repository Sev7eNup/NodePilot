import { Add, Send, TrashCan } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { sharedFoldersApi } from '../../api/sharedFolders';
import { ModalShell } from '../common/ModalShell';
import { ConditionBuilder, type ExprNode } from '../designer/ConditionBuilder';
import { NOTIFICATION_CHANNELS, type NotificationChannel } from '../../lib/eventFields';
import {
  systemAlertingApi,
  type SystemAlertSource, type SystemAlertPolicy, type SaveSystemAlertPolicyRequest, type SystemAlertPreviewResponse,
} from '../../api/systemAlerting';
import { UNCHANGED_SECRET, type TestFireResponse } from '../../api/alerting';
import { toast } from '../../stores/toastStore';

type Scope = 'Global' | 'Folders' | 'Workflows';

type RouteForm = {
  id: string | null;
  channel: NotificationChannel;
  target: string;
  hasStoredSecret: boolean;
  secretInput: string;
};

type FormState = {
  name: string;
  description: string;
  isEnabled: boolean;
  presetId: string;
  condition: ExprNode | null;
  params: Record<string, string>;
  sustainForSeconds: number;
  severityOverride: string;
  scope: Scope;
  folderIds: string[];
  workflowIds: string[];
  cooldownMinutes: number;
  routes: RouteForm[];
};

function parse(json: string | null | undefined): ExprNode | null {
  if (!json) return null;
  try { return JSON.parse(json) as ExprNode; } catch { return null; }
}

function fromPolicy(source: SystemAlertSource, policy: SystemAlertPolicy | null): FormState {
  const params: Record<string, string> = {};
  for (const p of source.parameters) {
    const v = policy?.sourceParameters?.[p.name] ?? p.default;
    params[p.name] = v === null || v === undefined ? '' : String(v);
  }
  if (!policy) {
    return {
      name: '', description: '', isEnabled: false, presetId: '',
      condition: null, params, sustainForSeconds: 0, severityOverride: '',
      scope: 'Global', folderIds: [], workflowIds: [], cooldownMinutes: 0,
      routes: [{ id: null, channel: 'Email', target: '', hasStoredSecret: false, secretInput: '' }],
    };
  }
  return {
    name: policy.name,
    description: policy.description ?? '',
    isEnabled: policy.isEnabled,
    presetId: policy.presetId ?? '',
    condition: parse(policy.conditionJson),
    params,
    sustainForSeconds: policy.sustainForSeconds,
    severityOverride: policy.severityOverride ?? '',
    scope: (policy.scopeKind as Scope) ?? 'Global',
    folderIds: policy.targets.filter((t) => t.targetKind === 'Folder').map((t) => t.targetId),
    workflowIds: policy.targets.filter((t) => t.targetKind === 'Workflow').map((t) => t.targetId),
    cooldownMinutes: policy.cooldownMinutes,
    routes: policy.routes.length
      ? policy.routes.map((r) => ({
          id: r.id, channel: (r.channel as NotificationChannel) ?? 'Email', target: r.target,
          hasStoredSecret: r.secret === UNCHANGED_SECRET, secretInput: '',
        }))
      : [{ id: null, channel: 'Email', target: '', hasStoredSecret: false, secretInput: '' }],
  };
}

export function SystemPolicyEditor({
  source, policy, onClose,
}: Readonly<{ source: SystemAlertSource; policy: SystemAlertPolicy | null; onClose: () => void }>) {
  const { t } = useTranslation(['alerts', 'common']);
  const qc = useQueryClient();
  const [form, setForm] = useState<FormState>(() => fromPolicy(source, policy));
  const [testResult, setTestResult] = useState<TestFireResponse | null>(null);
  const [previewResult, setPreviewResult] = useState<SystemAlertPreviewResponse | null>(null);

  const scopeable = source.scopeCapability === 'WorkflowScoped';
  const { data: folders } = useQuery({ queryKey: ['shared-folders'], queryFn: () => sharedFoldersApi.list(), enabled: scopeable });
  const { data: workflows } = useQuery({
    queryKey: ['workflows-min'], enabled: scopeable,
    queryFn: () => api.get<Array<{ id: string; name: string }>>('/workflows'),
  });

  const eventFields = useMemo(() => source.fields.map((f) => ({ name: f.name, label: f.unit ? `${f.name} (${f.unit})` : f.name })), [source.fields]);

  const applyPreset = (presetId: string) => {
    const preset = source.presets.find((p) => p.presetId === presetId);
    setForm((f) => {
      if (!preset) return { ...f, presetId: '' };
      const params = { ...f.params };
      for (const [k, v] of Object.entries(preset.parameters ?? {})) params[k] = String(v);
      return {
        ...f, presetId,
        condition: parse(preset.conditionJson) ?? f.condition,
        sustainForSeconds: preset.sustainForSeconds,
        severityOverride: preset.severity,
        params,
      };
    });
  };

  const buildBody = (f: FormState): SaveSystemAlertPolicyRequest => {
    const scope: Scope = scopeable ? f.scope : 'Global';
    const sourceParameters: Record<string, unknown> = {};
    for (const p of source.parameters) {
      const raw = f.params[p.name];
      if (raw === undefined || raw === '') continue;
      sourceParameters[p.name] = p.type === 'Number' || p.type === 'Duration' ? Number(raw) : raw;
    }
    return {
      name: f.name.trim(),
      description: f.description.trim() || null,
      isEnabled: f.isEnabled,
      sourceId: source.sourceId,
      presetId: f.presetId || null,
      sourceParameters: Object.keys(sourceParameters).length ? sourceParameters : null,
      conditionJson: f.condition ? JSON.stringify(f.condition) : null,
      sustainForSeconds: Math.max(0, f.sustainForSeconds),
      severityOverride: f.severityOverride || null,
      scopeKind: scope,
      cooldownMinutes: Math.max(0, f.cooldownMinutes),
      minOccurrences: 1,
      occurrenceWindowMinutes: 0,
      routes: f.routes.map((r, i) => ({
        id: r.id, channel: r.channel, target: r.target.trim(),
        secret: r.channel !== 'GenericWebhook' ? null : (r.secretInput ? r.secretInput : (r.hasStoredSecret ? UNCHANGED_SECRET : null)),
        order: i, conditionExpressionJson: null,
      })),
      targets:
        scope === 'Folders' ? f.folderIds.map((id) => ({ targetKind: 'Folder', targetId: id }))
        : scope === 'Workflows' ? f.workflowIds.map((id) => ({ targetKind: 'Workflow', targetId: id }))
        : [],
    };
  };

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['system-alert-policies'] });
    qc.invalidateQueries({ queryKey: ['system-alert-catalog'] });
  };

  const saveMutation = useMutation({
    mutationFn: async (f: FormState) => {
      const body = buildBody(f);
      if (policy) await systemAlertingApi.update(policy.id, body);
      else await systemAlertingApi.create(body);
    },
    onSuccess: () => { invalidate(); onClose(); },
    onError: (err: Error) => toast.error(t('alerts:system.editor.saveFailed', { message: err.message })),
  });

  const previewMutation = useMutation({
    mutationFn: (f: FormState) => {
      const body = buildBody(f);
      return systemAlertingApi.preview({ sourceId: source.sourceId, sourceParameters: body.sourceParameters, conditionJson: body.conditionJson });
    },
    onSuccess: (res) => setPreviewResult(res),
    onError: (err: Error) => toast.error(err.message),
  });

  const testFireMutation = useMutation({
    mutationFn: () => systemAlertingApi.testFire(policy!.id),
    onSuccess: (res) => setTestResult(res),
    onError: (err: Error) => toast.error(t('alerts:testFireFailed', { message: err.message })),
  });

  const updateRoute = (idx: number, patch: Partial<RouteForm>) =>
    setForm((f) => ({ ...f, routes: f.routes.map((r, i) => (i === idx ? { ...r, ...patch } : r)) }));
  const addRoute = () => setForm((f) => ({ ...f, routes: [...f.routes, { id: null, channel: 'Email', target: '', hasStoredSecret: false, secretInput: '' }] }));
  const removeRoute = (idx: number) => setForm((f) => ({ ...f, routes: f.routes.filter((_, i) => i !== idx) }));
  const toggleId = (key: 'folderIds' | 'workflowIds', id: string) =>
    setForm((f) => ({ ...f, [key]: f[key].includes(id) ? f[key].filter((x) => x !== id) : [...f[key], id] }));

  const formValid = useMemo(() => {
    if (!form.name.trim()) return false;
    if (form.isEnabled && (form.routes.length === 0 || form.routes.some((r) => !r.target.trim()))) return false;
    if (scopeable && form.scope === 'Folders' && form.folderIds.length === 0) return false;
    if (scopeable && form.scope === 'Workflows' && form.workflowIds.length === 0) return false;
    return true;
  }, [form, scopeable]);

  const label = t(`alerts:system.sourceLabels.${source.sourceId}`, source.sourceId);

  return (
    <ModalShell onClose={onClose} maxWidth="max-w-2xl">
      <h3 className="text-lg font-semibold mb-1 text-on-surface">
        {policy ? t('alerts:system.editPolicy') : t('alerts:system.newPolicy')}
      </h3>
      <p className="text-xs text-on-surface-variant mb-4">{label}</p>
      <div className="space-y-4 max-h-[72vh] overflow-y-auto pr-1">
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.name')}</label>
          <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })}
            placeholder={t('alerts:system.editor.namePlaceholder')}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.description')}</label>
          <input type="text" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
        </div>
        <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
          <input type="checkbox" checked={form.isEnabled} onChange={(e) => setForm({ ...form, isEnabled: e.target.checked })} className="rounded" />
          {t('alerts:system.editor.enabled')}
          <span className="text-[11px] text-outline">— {t('alerts:system.editor.enabledHint')}</span>
        </label>

        {source.presets.length > 0 && (
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.preset')}</label>
            <select value={form.presetId} onChange={(e) => applyPreset(e.target.value)}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
              <option value="">{t('alerts:system.editor.presetNone')}</option>
              {source.presets.map((p) => <option key={p.presetId} value={p.presetId}>{p.presetId}</option>)}
            </select>
          </div>
        )}

        {/* Condition */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.condition')}</label>
          <div className="border border-outline-variant rounded-md p-2 bg-surface-lowest">
            <ConditionBuilder value={form.condition} upstreamVars={[]} eventFields={eventFields}
              onChange={(next) => setForm((f) => ({ ...f, condition: next }))} />
          </div>
          <p className="text-[11px] text-outline mt-1">{t('alerts:system.editor.conditionHint')}</p>
        </div>

        {/* Source parameters */}
        {source.parameters.length > 0 && (
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.sourceParams')}</label>
            <div className="grid grid-cols-2 gap-3">
              {source.parameters.map((p) => (
                <div key={p.name}>
                  <label className="block text-[11px] text-on-surface-variant mb-1">{p.name}{p.unit ? ` (${p.unit})` : ''}</label>
                  <input
                    type={p.type === 'Number' || p.type === 'Duration' ? 'number' : 'text'}
                    value={form.params[p.name] ?? ''}
                    min={p.min ?? undefined} max={p.max ?? undefined}
                    onChange={(e) => setForm((f) => ({ ...f, params: { ...f.params, [p.name]: e.target.value } }))}
                    className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Sustain + severity */}
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.sustain')}</label>
            <input type="number" min={0} value={form.sustainForSeconds}
              onChange={(e) => setForm({ ...form, sustainForSeconds: Math.max(0, Number(e.target.value) || 0) })}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.severity')}</label>
            <select value={form.severityOverride} onChange={(e) => setForm({ ...form, severityOverride: e.target.value })}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
              <option value="">{t('alerts:system.editor.severityDefault')}</option>
              <option value="Info">Info</option>
              <option value="Warning">Warning</option>
              <option value="Critical">Critical</option>
            </select>
          </div>
        </div>
        <p className="text-[11px] text-outline -mt-2">{t('alerts:system.editor.sustainHint')}</p>

        {/* Scope (WorkflowScoped sources only) */}
        {scopeable && (
          <>
            <div>
              <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.scope')}</label>
              <select value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value as Scope })}
                className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest">
                <option value="Global">{t('alerts:scopes.Global')}</option>
                <option value="Folders">{t('alerts:scopes.Folders')}</option>
                <option value="Workflows">{t('alerts:scopes.Workflows')}</option>
              </select>
            </div>
            {form.scope === 'Folders' && (
              <div className="max-h-40 overflow-y-auto border border-outline-variant rounded-md p-2 space-y-1">
                {(folders ?? []).map((fo) => (
                  <label key={fo.id} className="flex items-center gap-2 text-sm cursor-pointer">
                    <input type="checkbox" checked={form.folderIds.includes(fo.id)} onChange={() => toggleId('folderIds', fo.id)} className="rounded" />
                    <span className="font-mono text-xs">{fo.path}</span>
                  </label>
                ))}
              </div>
            )}
            {form.scope === 'Workflows' && (
              <div className="max-h-40 overflow-y-auto border border-outline-variant rounded-md p-2 space-y-1">
                {(workflows ?? []).map((wf) => (
                  <label key={wf.id} className="flex items-center gap-2 text-sm cursor-pointer">
                    <input type="checkbox" checked={form.workflowIds.includes(wf.id)} onChange={() => toggleId('workflowIds', wf.id)} className="rounded" />
                    <span className="truncate">{wf.name}</span>
                  </label>
                ))}
              </div>
            )}
          </>
        )}

        {/* Cooldown */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:system.editor.cooldown')}</label>
          <input type="number" min={0} value={form.cooldownMinutes}
            onChange={(e) => setForm({ ...form, cooldownMinutes: Math.max(0, Number(e.target.value) || 0) })}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
        </div>

        {/* Routes */}
        <div>
          <div className="flex items-center justify-between mb-1">
            <label className="block text-xs font-medium text-on-surface-variant">{t('alerts:system.editor.routes')}</label>
            <button type="button" onClick={addRoute}
              className="flex items-center gap-1 px-2 py-1 text-xs bg-primary/10 text-primary border border-primary/30 rounded hover:bg-primary/20">
              <Add size={12} /> {t('alerts:addRoute')}
            </button>
          </div>
          <div className="space-y-2">
            {form.routes.map((r, idx) => (
              <div key={idx} className="flex flex-wrap items-center gap-2 border border-outline-variant/60 rounded-md p-2 bg-surface-lowest">
                <select value={r.channel} onChange={(e) => updateRoute(idx, { channel: e.target.value as NotificationChannel })}
                  className="text-xs border border-outline-variant/50 rounded px-1 py-1.5 bg-surface-lowest">
                  {NOTIFICATION_CHANNELS.map((c) => <option key={c} value={c}>{t(`alerts:channels.${c}`)}</option>)}
                </select>
                <input type="text" value={r.target} onChange={(e) => updateRoute(idx, { target: e.target.value })}
                  placeholder={r.channel === 'Email' ? t('alerts:emailPlaceholder') : t('alerts:webhookPlaceholder')}
                  className="flex-1 min-w-[160px] px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
                {r.channel === 'GenericWebhook' && (
                  <input type="password" value={r.secretInput} onChange={(e) => updateRoute(idx, { secretInput: e.target.value })}
                    placeholder={r.hasStoredSecret ? t('alerts:secretStored') : t('alerts:secretPlaceholder')}
                    className="w-40 px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
                )}
                <button type="button" onClick={() => removeRoute(idx)} className="p-1.5 text-outline hover:text-red-600" title={t('common:delete')}>
                  <TrashCan size={14} />
                </button>
              </div>
            ))}
          </div>
        </div>

        {previewResult && (
          <div className={`rounded-md border p-2 text-xs ${previewResult.available ? 'border-emerald-500/40 bg-emerald-500/10' : 'border-amber-500/40 bg-amber-500/10'}`}>
            {!previewResult.available ? (
              <div>{t('alerts:system.editor.previewUnavailable')}</div>
            ) : previewResult.matches.filter((m) => m.matched).length === 0 ? (
              <div>{t('alerts:system.editor.previewNoMatch')}</div>
            ) : (
              <>
                <div className="font-semibold mb-1">
                  {t('alerts:system.editor.previewMatchCount', { count: previewResult.matches.filter((m) => m.matched).length })}
                </div>
                {previewResult.matches.filter((m) => m.matched).slice(0, 10).map((m) => (
                  <div key={m.instanceKey} className="font-mono truncate">✓ {m.title ?? m.instanceKey}</div>
                ))}
              </>
            )}
          </div>
        )}

        {testResult && (
          <div className={`rounded-md border p-2 text-xs ${testResult.allSucceeded ? 'border-emerald-500/40 bg-emerald-500/10' : 'border-red-500/40 bg-red-500/10'}`}>
            <div className="font-semibold mb-1">{testResult.allSucceeded ? t('alerts:testFireOk') : t('alerts:testFirePartial')}</div>
            {testResult.results.map((r, i) => (
              <div key={i} className="font-mono">{r.success ? '✓' : '✗'} {r.channel} → {r.target}{r.error ? `: ${r.error}` : ''}</div>
            ))}
          </div>
        )}
      </div>
      <div className="flex justify-between items-center gap-2 mt-5">
        <div className="flex gap-2">
          <button onClick={() => previewMutation.mutate(form)} disabled={previewMutation.isPending}
            className="px-3 py-2 text-sm border border-outline-variant rounded-md text-on-surface hover:bg-surface-container disabled:opacity-50">
            {previewMutation.isPending ? t('alerts:system.editor.previewing') : t('alerts:system.editor.preview')}
          </button>
          {policy && (
            <button onClick={() => testFireMutation.mutate()} disabled={testFireMutation.isPending}
              className="flex items-center gap-1.5 px-3 py-2 text-sm border border-outline-variant rounded-md text-on-surface hover:bg-surface-container disabled:opacity-50">
              <Send size={14} /> {testFireMutation.isPending ? t('alerts:testFiring') : t('alerts:testFire')}
            </button>
          )}
        </div>
        <div className="flex gap-2">
          <button onClick={onClose} className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md">
            {t('common:cancel')}
          </button>
          <button onClick={() => saveMutation.mutate(form)} disabled={!formValid || saveMutation.isPending}
            className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50">
            {saveMutation.isPending ? t('alerts:system.editor.saving') : t('alerts:system.editor.save')}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}
