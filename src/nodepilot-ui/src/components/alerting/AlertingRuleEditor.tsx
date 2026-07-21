import { Add, Send, TrashCan } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { sharedFoldersApi } from '../../api/sharedFolders';
import { ModalShell } from '../common/ModalShell';
import { ConditionBuilder, type ExprNode } from '../designer/ConditionBuilder';
import {
  alertingApi, UNCHANGED_SECRET,
  type NotificationRule, type PreviewRuleResponse, type SaveNotificationRuleRequest, type TestFireResponse,
} from '../../api/alerting';
import {
  customEventFields, NOTIFICATION_EVENT_TYPES, NOTIFICATION_CHANNELS,
  type NotificationChannel,
} from '../../lib/eventFields';
import { toast } from '../../stores/toastStore';

type Scope = 'Global' | 'Folders' | 'Workflows';

type RouteForm = {
  id: string | null;
  channel: NotificationChannel;
  target: string;
  hasStoredSecret: boolean;
  secretInput: string;
  condition: ExprNode | null;
};

type FormState = {
  name: string;
  description: string;
  isEnabled: boolean;
  eventTypes: string[];
  scope: Scope;
  folderIds: string[];
  workflowIds: string[];
  filter: ExprNode | null;
  dedupKeyTemplate: string;
  routes: RouteForm[];
  cooldownMinutes: number;
  minOccurrences: number;
  occurrenceWindowMinutes: number;
};

function parseFilter(json: string | null): ExprNode | null {
  if (!json) return null;
  try { return JSON.parse(json) as ExprNode; } catch { return null; }
}

function fromRule(rule: NotificationRule | null): FormState {
  if (!rule) {
    return {
      // New rules start DISABLED — an operator opts in explicitly after reviewing routes/scope.
      name: '', description: '', isEnabled: false,
      eventTypes: ['ExecutionFailed'],
      scope: 'Global', folderIds: [], workflowIds: [],
      filter: null,
      dedupKeyTemplate: '',
      routes: [{ id: null, channel: 'Email', target: '', hasStoredSecret: false, secretInput: '', condition: null }],
      cooldownMinutes: 0, minOccurrences: 1, occurrenceWindowMinutes: 0,
    };
  }
  return {
    name: rule.name,
    description: rule.description ?? '',
    isEnabled: rule.isEnabled,
    eventTypes: rule.eventTypes,
    scope: (rule.scopeKind as Scope) ?? 'Global',
    folderIds: rule.targets.filter((t) => t.targetKind === 'Folder').map((t) => t.targetId),
    workflowIds: rule.targets.filter((t) => t.targetKind === 'Workflow').map((t) => t.targetId),
    filter: parseFilter(rule.filterExpressionJson),
    dedupKeyTemplate: rule.dedupKeyTemplate ?? '',
    routes: rule.routes.map((r) => ({
      id: r.id,
      channel: (r.channel as NotificationChannel) ?? 'Email',
      target: r.target,
      hasStoredSecret: r.secret === UNCHANGED_SECRET,
      secretInput: '',
      condition: parseFilter(r.conditionExpressionJson ?? null),
    })),
    cooldownMinutes: rule.cooldownMinutes,
    minOccurrences: rule.minOccurrences,
    occurrenceWindowMinutes: rule.occurrenceWindowMinutes,
  };
}

export function AlertingRuleEditor({
  rule, onClose,
}: Readonly<{ rule: NotificationRule | null; onClose: () => void }>) {
  const { t } = useTranslation(['alerts', 'common']);
  const queryClient = useQueryClient();
  const [form, setForm] = useState<FormState>(() => fromRule(rule));
  const [testResult, setTestResult] = useState<TestFireResponse | null>(null);
  const [previewResult, setPreviewResult] = useState<PreviewRuleResponse | null>(null);

  const { data: catalog } = useQuery({ queryKey: ['alerting-catalog'], queryFn: () => alertingApi.catalog() });
  const { data: folders } = useQuery({ queryKey: ['shared-folders'], queryFn: () => sharedFoldersApi.list() });
  const { data: workflows } = useQuery({
    queryKey: ['workflows-min'],
    queryFn: () => api.get<Array<{ id: string; name: string }>>('/workflows'),
  });

  const eventFieldOptions = useMemo(
    () => customEventFields().map((f) => ({ name: f.name, label: t(`alerts:${f.labelKey}`) })),
    [t],
  );
  const availableEventTypes = catalog?.eventTypes.map((e) => e.name) ?? [...NOTIFICATION_EVENT_TYPES];
  const availableChannels = catalog?.channels ?? [...NOTIFICATION_CHANNELS];

  const buildBody = (f: FormState): SaveNotificationRuleRequest => {
    const scope: Scope = f.scope;
    return {
      name: f.name.trim(),
      description: f.description.trim() || null,
      isEnabled: f.isEnabled,
      eventTypes: f.eventTypes,
      filterExpressionJson: f.filter ? JSON.stringify(f.filter) : null,
      scopeKind: scope,
      cooldownMinutes: f.cooldownMinutes,
      minOccurrences: f.minOccurrences,
      occurrenceWindowMinutes: f.occurrenceWindowMinutes,
      dedupKeyTemplate: f.dedupKeyTemplate.trim() || null,
      routes: f.routes.map((r, i) => ({
        id: r.id,
        channel: r.channel,
        target: r.target.trim(),
        // Only webhook routes carry a secret. Guard on channel so a secret typed on a webhook route
        // that was later switched to Email is not persisted as orphaned dead data.
        secret: r.channel !== 'GenericWebhook'
          ? null
          : r.secretInput ? r.secretInput : (r.hasStoredSecret ? UNCHANGED_SECRET : null),
        order: i,
        conditionExpressionJson: r.condition ? JSON.stringify(r.condition) : null,
      })),
      targets:
        scope === 'Folders' ? f.folderIds.map((id) => ({ targetKind: 'Folder', targetId: id }))
        : scope === 'Workflows' ? f.workflowIds.map((id) => ({ targetKind: 'Workflow', targetId: id }))
        : [],
    };
  };

  const buildPreviewEventFields = (f: FormState): Record<string, string> => {
    const firstType = f.eventTypes[0] ?? 'ExecutionFailed';
    return {
      eventType: firstType,
      severity: firstType === 'ExecutionSucceeded' ? 'Info' : 'Warning',
      workflowName: 'Sample Workflow',
      folderPath: '/',
      status:
        firstType === 'ExecutionSucceeded' ? 'Succeeded'
        : firstType === 'ExecutionCancelled' ? 'Cancelled'
        : 'Failed',
      errorMessage: firstType === 'ExecutionFailed' ? 'Sample failure' : '',
      durationMs: '1234',
      triggeredBy: 'admin',
      callDepth: '0',
      isSubWorkflow: 'false',
      cancelledBy: firstType === 'ExecutionCancelled' ? 'user' : '',
      sourceKey: '',
      targetMachine: '',
      signalValue: '',
    };
  };

  const saveMutation = useMutation({
    mutationFn: async (f: FormState) => {
      const body = buildBody(f);
      if (rule) await alertingApi.update(rule.id, body);
      else await alertingApi.create(body);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['alerting-rules'] });
      onClose();
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const testFireMutation = useMutation({
    mutationFn: () => alertingApi.testFire(rule!.id),
    onSuccess: (res) => setTestResult(res),
    onError: (err: Error) => toast.error(t('alerts:testFireFailed', { message: err.message })),
  });

  const previewMutation = useMutation({
    mutationFn: async (f: FormState) => {
      const body = buildBody(f);
      return alertingApi.previewRule({
        eventTypes: body.eventTypes,
        filterExpressionJson: body.filterExpressionJson,
        scopeKind: body.scopeKind,
        routes: body.routes,
        targets: body.targets,
        dedupKeyTemplate: body.dedupKeyTemplate,
        eventFields: buildPreviewEventFields(f),
      });
    },
    onSuccess: (res) => setPreviewResult(res),
    onError: (err: Error) => toast.error(t('alerts:previewFailed', { message: err.message })),
  });

  const toggleEventType = (et: string) =>
    setForm((f) => ({
      ...f,
      eventTypes: f.eventTypes.includes(et) ? f.eventTypes.filter((x) => x !== et) : [...f.eventTypes, et],
    }));
  const toggleId = (key: 'folderIds' | 'workflowIds', id: string) =>
    setForm((f) => ({ ...f, [key]: f[key].includes(id) ? f[key].filter((x) => x !== id) : [...f[key], id] }));

  const updateRoute = (idx: number, patch: Partial<RouteForm>) =>
    setForm((f) => ({ ...f, routes: f.routes.map((r, i) => (i === idx ? { ...r, ...patch } : r)) }));
  const addRoute = () =>
    setForm((f) => ({ ...f, routes: [...f.routes, { id: null, channel: 'Email', target: '', hasStoredSecret: false, secretInput: '', condition: null }] }));
  const removeRoute = (idx: number) =>
    setForm((f) => ({ ...f, routes: f.routes.filter((_, i) => i !== idx) }));

  const formValid = useMemo(() => {
    if (!form.name.trim()) return false;
    if (form.eventTypes.length === 0) return false;
    if (form.routes.length === 0 || form.routes.some((r) => !r.target.trim())) return false;
    if (form.scope === 'Folders' && form.folderIds.length === 0) return false;
    if (form.scope === 'Workflows' && form.workflowIds.length === 0) return false;
    if (form.cooldownMinutes < 0 || form.occurrenceWindowMinutes < 0 || form.minOccurrences < 1) return false;
    return true;
  }, [form]);

  return (
    <ModalShell onClose={onClose} maxWidth="max-w-2xl">
      <h3 className="text-lg font-semibold mb-4 text-on-surface">
        {rule ? t('alerts:editTitle') : t('alerts:createTitle')}
      </h3>
      <div className="space-y-4 max-h-[72vh] overflow-y-auto pr-1">
        {/* Name + description + enabled */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.name')}</label>
          <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.description')}</label>
          <input type="text" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
        </div>
        <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
          <input type="checkbox" checked={form.isEnabled} onChange={(e) => setForm({ ...form, isEnabled: e.target.checked })} className="rounded" />
          {t('alerts:fields.enabled')}
        </label>

        {/* Event types */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.eventTypes')}</label>
          <div className="flex flex-wrap gap-1">
            {availableEventTypes.map((et) => (
              <button key={et} type="button" onClick={() => toggleEventType(et)}
                className={`px-2 py-1 rounded text-xs font-medium border ${
                  form.eventTypes.includes(et)
                    ? 'bg-blue-600 text-white border-blue-600'
                    : 'border-outline-variant text-on-surface-variant hover:bg-surface-container'}`}>
                {t(`alerts:eventTypeLabels.${et}`)}
              </button>
            ))}
          </div>
        </div>

        {/* Scope — custom rules can target Global / specific folders / specific workflows */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.scope')}</label>
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

        {/* Filter (ConditionBuilder, event mode) */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.filter')}</label>
          <div className="border border-outline-variant rounded-md p-2 bg-surface-lowest">
            <ConditionBuilder
              value={form.filter}
              upstreamVars={[]}
              eventFields={eventFieldOptions}
              onChange={(next) => setForm((f) => ({ ...f, filter: next }))}
            />
          </div>
          <p className="text-[11px] text-outline mt-1">{t('alerts:filterHint')}</p>
        </div>

        {/* Grouping / dedup key */}
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.dedupKeyTemplate')}</label>
          <input type="text" value={form.dedupKeyTemplate} onChange={(e) => setForm({ ...form, dedupKeyTemplate: e.target.value })}
            placeholder="{{eventType}}:{{workflowId}}"
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono" />
          <p className="text-[11px] text-outline mt-1">{t('alerts:dedupHint')}</p>
        </div>

        {/* Routes */}
        <div>
          <div className="flex items-center justify-between mb-1">
            <label className="block text-xs font-medium text-on-surface-variant">{t('alerts:fields.routes')}</label>
            <button type="button" onClick={addRoute}
              className="flex items-center gap-1 px-2 py-1 text-xs bg-primary/10 text-primary border border-primary/30 rounded hover:bg-primary/20">
              <Add size={12} /> {t('alerts:addRoute')}
            </button>
          </div>
          <div className="space-y-2">
            {form.routes.map((r, idx) => (
              <div key={idx} className="flex flex-wrap items-start gap-2 border border-outline-variant/60 rounded-md p-2 bg-surface-lowest">
                <select value={r.channel} onChange={(e) => updateRoute(idx, { channel: e.target.value as NotificationChannel })}
                  className="text-xs border border-outline-variant/50 rounded px-1 py-1.5 bg-surface-lowest">
                  {availableChannels.map((c) => (
                    <option key={c} value={c}>{t(`alerts:channels.${c}`)}</option>
                  ))}
                </select>
                <input type="text" value={r.target} onChange={(e) => updateRoute(idx, { target: e.target.value })}
                  placeholder={r.channel === 'Email' ? t('alerts:emailPlaceholder') : t('alerts:webhookPlaceholder')}
                  className="flex-1 min-w-[160px] px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
                {r.channel === 'GenericWebhook' && (
                  <div className="flex items-center gap-1">
                    <input type="password" value={r.secretInput} onChange={(e) => updateRoute(idx, { secretInput: e.target.value })}
                      placeholder={r.hasStoredSecret ? t('alerts:secretStored') : t('alerts:secretPlaceholder')}
                      className="w-40 px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
                    {r.hasStoredSecret && !r.secretInput && (
                      <button type="button" onClick={() => updateRoute(idx, { hasStoredSecret: false, secretInput: '' })}
                        className="text-[11px] px-1.5 py-1 text-outline hover:text-red-600 whitespace-nowrap" title={t('alerts:clearSecret')}>
                        {t('alerts:clearSecret')}
                      </button>
                    )}
                  </div>
                )}
                <button type="button" onClick={() => removeRoute(idx)} className="p-1.5 text-outline hover:text-red-600" title={t('common:delete')}>
                  <TrashCan size={14} />
                </button>
                <div className="basis-full border-t border-outline-variant/50 pt-2">
                  <label className="block text-[11px] font-medium text-on-surface-variant mb-1">{t('alerts:fields.routeCondition')}</label>
                  <ConditionBuilder
                    value={r.condition}
                    upstreamVars={[]}
                    eventFields={eventFieldOptions}
                    onChange={(next) => updateRoute(idx, { condition: next })}
                  />
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Throttle */}
        <div className="grid grid-cols-3 gap-3">
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.cooldownMinutes')}</label>
            <input type="number" min={0} value={form.cooldownMinutes}
              onChange={(e) => setForm({ ...form, cooldownMinutes: Math.max(0, Number(e.target.value) || 0) })}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.minOccurrences')}</label>
            <input type="number" min={1} value={form.minOccurrences}
              onChange={(e) => setForm({ ...form, minOccurrences: Math.max(1, Number(e.target.value) || 1) })}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
          </div>
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('alerts:fields.occurrenceWindowMinutes')}</label>
            <input type="number" min={0} value={form.occurrenceWindowMinutes}
              onChange={(e) => setForm({ ...form, occurrenceWindowMinutes: Math.max(0, Number(e.target.value) || 0) })}
              className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm" />
          </div>
        </div>
        <p className="text-[11px] text-outline">{t('alerts:throttleHint')}</p>

        {/* Test-fire result */}
        {testResult && (
          <div className={`rounded-md border p-2 text-xs ${testResult.allSucceeded ? 'border-emerald-500/40 bg-emerald-500/10' : 'border-red-500/40 bg-red-500/10'}`}>
            <div className="font-semibold mb-1">{testResult.allSucceeded ? t('alerts:testFireOk') : t('alerts:testFirePartial')}</div>
            {testResult.results.map((r, i) => (
              <div key={i} className="font-mono">
                {r.success ? '✓' : '✗'} {r.channel} → {r.target}{r.error ? `: ${r.error}` : ''}
              </div>
            ))}
          </div>
        )}

        {previewResult && (
          <div className={`rounded-md border p-2 text-xs ${previewResult.matchesRule ? 'border-emerald-500/40 bg-emerald-500/10' : 'border-amber-500/40 bg-amber-500/10'}`}>
            <div className="font-semibold mb-1">
              {previewResult.matchesRule ? t('alerts:previewMatches') : t('alerts:previewNoMatch')}
            </div>
            {previewResult.dedupKey && (
              <div className="font-mono text-[11px] break-all">{t('alerts:previewDedup')}: {previewResult.dedupKey}</div>
            )}
            {previewResult.reasons.map((reason) => (
              <div key={reason} className="text-outline">{reason}</div>
            ))}
            {previewResult.routes.map((r, i) => (
              <div key={`${r.channel}-${r.target}-${i}`} className="font-mono">
                {r.matches ? 'send' : 'skip'} {r.channel} - {r.target}
              </div>
            ))}
          </div>
        )}
      </div>
      <div className="flex justify-between items-center gap-2 mt-5">
        <div className="flex gap-2">
          <button onClick={() => previewMutation.mutate(form)} disabled={!formValid || previewMutation.isPending}
            className="flex items-center gap-1.5 px-3 py-2 text-sm border border-outline-variant rounded-md text-on-surface hover:bg-surface-container disabled:opacity-50">
            {previewMutation.isPending ? t('alerts:previewing') : t('alerts:preview')}
          </button>
          {rule && (
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
            {saveMutation.isPending ? t('common:saving') : (rule ? t('common:update') : t('common:create'))}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}
