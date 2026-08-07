import { Add, Bot, Chip, Email, Locked, Send, TrashCan } from '@carbon/icons-react';
import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  adminSettings,
  SettingsApiError,
  type SettingsSectionResponse,
} from '../../api/adminSettings';
import { SecretField, serializeSecretField, type SecretFieldMode } from './SecretField';
import { EnvOverrideBadge } from './EnvOverrideBadge';
import { EtagConflictDialog } from './EtagConflictDialog';
import { TestProbeModal } from './TestProbeModal';
import { HotReloadHint } from './SectionFormHelpers';
import { refreshAiCapabilities } from '../../hooks/useAiCapabilities';

type SmtpDto = {
  host: string;
  port: number;
  username: string | null;
  password: string | null;
  from: string;
  enableSsl: boolean;
};

type LlmProfileDto = {
  id: string;
  name: string;
  baseUrl: string;
  apiKey: string | null;
  model: string;
  maxTokens: number;
  timeoutSeconds: number;
  enableToolCalling: boolean;
  toolCallMaxDepth: number;
  /**
   * Response-only. Non-null ⇒ the profile also exists in a configuration source below the runtime
   * overrides file, so deleting it here wouldn't stick — the entry would resurface on the next
   * reload. Editing still works (the override wins).
   */
  managedBy: string | null;
};

type LlmDto = {
  enabled: boolean;
  activeProfileId: string;
  profiles: LlmProfileDto[];
};

/** Draft secret state per profile id — SecretField is stateless, the parent owns mode+value. */
type SecretDraft = { mode: SecretFieldMode; value: string };

/**
 * Profile ids become configuration key segments (`Llm:Profiles:<id>:ApiKey`) and are immutable
 * once assigned, so they're derived from the name only at creation time and then left alone.
 */
function slugifyProfileId(name: string, taken: ReadonlySet<string>): string {
  const base = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 32) || 'profile';
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) {
    const candidate = `${base.slice(0, 29)}-${n}`;
    if (!taken.has(candidate)) return candidate;
  }
}

function uniqueProfileName(base: string, taken: ReadonlySet<string>): string {
  if (!taken.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) {
    const candidate = `${base} ${n}`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}

/**
 * V1 integrations tab: SMTP + LLM sections rendered side-by-side. Each card follows the
 * same structure (form, secret field, test button, save button) so a future Auth or
 * Retention card can be added with minimal copy-paste. Save uses ETag/If-Match: a 412
 * surfaces the {@link EtagConflictDialog}, a 400 surfaces the field-level validation
 * errors inline.
 */
export function IntegrationsSection() {
  return (
    <div className="space-y-4">
      <SmtpCard />
      <LlmCard />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// SMTP
// ─────────────────────────────────────────────────────────────────────────────

function SmtpCard() {
  const { t } = useTranslation(['adminSettings', 'common']);
  const queryClient = useQueryClient();
  const [showTest, setShowTest] = useState(false);
  const [testTo, setTestTo] = useState('');
  const [conflict, setConflict] = useState<SettingsSectionResponse<SmtpDto> | null>(null);
  const [error, setError] = useState<string[] | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', 'Smtp'],
    queryFn: () => adminSettings.getSection<SmtpDto>('Smtp'),
  });

  const [form, setForm] = useState<SmtpDto>({
    host: '', port: 25, username: null, password: null, from: '', enableSsl: true,
  });
  const [pwMode, setPwMode] = useState<SecretFieldMode>('keep');
  const [pwValue, setPwValue] = useState('');

  // Sync local draft when a fresh server snapshot arrives (initial load or after Save).
  useEffect(() => {
    if (!data) return;
    setForm(data.payload);
    setPwMode(data.payload.password ? 'keep' : 'change');
    setPwValue('');
  }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const buildPayload = () => ({
    Host: form.host,
    Port: form.port,
    Username: form.username,
    From: form.from,
    Password: serializeSecretField(pwMode, pwValue),
    EnableSsl: form.enableSsl,
  });

  const saveMutation = useMutation({
    mutationFn: async () => {
      setError(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<SmtpDto>('Smtp', buildPayload(), data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', 'Smtp'], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<SmtpDto>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        setError(err.body.errors.map((e) => e.message ?? JSON.stringify(e)));
        return;
      }
      setError([err instanceof Error ? err.message : String(err)]);
    },
  });

  if (isLoading || !data) {
    return <Card icon={Email} title={t('adminSettings:subTabIntegrations')}><p className="text-sm">{t('adminSettings:loading')}</p></Card>;
  }

  return (
    <>
      <Card icon={Email} title="SMTP">
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <LabeledInput
            label="Host" configKey="Smtp:Host" effectiveSource={data.effectiveSource}
            value={form.host}
            onChange={(v) => setForm({ ...form, host: v })}
            disabled={isEnvLocked('Smtp:Host')}
          />
          <LabeledInput
            label="Port" configKey="Smtp:Port" effectiveSource={data.effectiveSource}
            type="number" value={String(form.port)}
            onChange={(v) => setForm({ ...form, port: Number.parseInt(v, 10) || 0 })}
            disabled={isEnvLocked('Smtp:Port')}
          />
          <LabeledInput
            label={t('adminSettings:integrations.from')} configKey="Smtp:From" effectiveSource={data.effectiveSource}
            value={form.from}
            onChange={(v) => setForm({ ...form, from: v })}
            disabled={isEnvLocked('Smtp:From')}
          />
          <LabeledInput
            label={t('common:username')} configKey="Smtp:Username" effectiveSource={data.effectiveSource}
            value={form.username ?? ''}
            onChange={(v) => setForm({ ...form, username: v || null })}
            disabled={isEnvLocked('Smtp:Username')}
          />
          <div className="md:col-span-2">
            <SecretField
              inputId="smtp-password"
              label={t('common:password')}
              hasPersistedValue={!!data.payload.password}
              mode={pwMode}
              value={pwValue}
              onModeChange={setPwMode}
              onValueChange={setPwValue}
              disabled={isEnvLocked('Smtp:Password')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Smtp:Password'] ?? ''} configKey="Smtp:Password" />
          </div>
          <div className="md:col-span-2">
            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={form.enableSsl}
                onChange={(e) => setForm({ ...form, enableSsl: e.target.checked })}
                disabled={isEnvLocked('Smtp:EnableSsl')}
                className="rounded"
              />
              {t('adminSettings:smtpEnableSslLabel')}
              <EnvOverrideBadge source={data.effectiveSource['Smtp:EnableSsl'] ?? ''} configKey="Smtp:EnableSsl" />
            </label>
            {!form.enableSsl && !!form.username && (
              <p className="mt-1 text-xs text-amber-700 dark:text-amber-300">
                {t('adminSettings:smtpEnableSslWarning')}
              </p>
            )}
          </div>
        </div>

        <SaveActions
          onSave={() => saveMutation.mutate()}
          onTest={() => setShowTest(true)}
          saving={saveMutation.isPending}
          errors={error}
        />
      </Card>
      <EtagConflictDialog
        open={!!conflict}
        serverSnapshot={conflict}
        localDraft={buildPayload()}
        onKeepMine={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Smtp'], conflict);
          setConflict(null);
          // Retry the save using the fresh ETag.
          adminSettings.putSection<SmtpDto>('Smtp', buildPayload(), conflict.etag)
            .then((fresh) => queryClient.setQueryData(['admin-settings', 'Smtp'], fresh))
            .catch((e: unknown) => setError([e instanceof Error ? e.message : String(e)]));
        }}
        onTakeTheirs={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Smtp'], conflict);
          setConflict(null);
        }}
        onCancel={() => setConflict(null)}
      />
      <TestProbeModal
        title={t('adminSettings:testProbeTitle')}
        open={showTest}
        onClose={() => setShowTest(false)}
        runProbe={() => adminSettings.testSmtp({ Settings: { ...buildPayload() }, ToAddress: testTo || null })}
      >
        <div>
          <label className="block text-xs text-on-surface-variant mb-1">{t('adminSettings:integrations.toAddress')}</label>
          <input
            type="email"
            value={testTo}
            onChange={(e) => setTestTo(e.target.value)}
            placeholder={form.from}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
      </TestProbeModal>
    </>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// LLM
// ─────────────────────────────────────────────────────────────────────────────

function LlmCard() {
  const { t } = useTranslation(['adminSettings']);
  const queryClient = useQueryClient();
  const [showTest, setShowTest] = useState(false);
  const [conflict, setConflict] = useState<SettingsSectionResponse<LlmDto> | null>(null);
  const [error, setError] = useState<string[] | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', 'Llm'],
    queryFn: () => adminSettings.getSection<LlmDto>('Llm'),
  });

  const [form, setForm] = useState<LlmDto>({ enabled: false, activeProfileId: '', profiles: [] });
  const [secrets, setSecrets] = useState<Record<string, SecretDraft>>({});
  const [selectedId, setSelectedId] = useState<string>('');

  useEffect(() => {
    if (!data) return;
    setForm(data.payload);
    // One secret draft per profile: a fresh snapshot means every pending key edit is stale.
    setSecrets(Object.fromEntries(data.payload.profiles.map((p) => [
      p.id, { mode: p.apiKey ? 'keep' : 'change', value: '' } satisfies SecretDraft,
    ])));
    setSelectedId((current) =>
      data.payload.profiles.some((p) => p.id === current)
        ? current
        : data.payload.activeProfileId || data.payload.profiles[0]?.id || '');
  }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const selected = form.profiles.find((p) => p.id === selectedId) ?? null;
  const secretOf = (id: string): SecretDraft => secrets[id] ?? { mode: 'change', value: '' };

  const patchProfile = (id: string, patch: Partial<LlmProfileDto>) =>
    setForm((f) => ({ ...f, profiles: f.profiles.map((p) => (p.id === id ? { ...p, ...patch } : p)) }));

  const addProfile = () => {
    const takenIds = new Set(form.profiles.map((p) => p.id));
    const takenNames = new Set(form.profiles.map((p) => p.name.toLowerCase()));
    const name = uniqueProfileName(t('adminSettings:integrations.newProfileName'), takenNames);
    const id = slugifyProfileId(name, takenIds);
    const profile: LlmProfileDto = {
      id, name,
      baseUrl: 'https://api.openai.com/v1',
      apiKey: null, model: '', maxTokens: 4096, timeoutSeconds: 90,
      enableToolCalling: false, toolCallMaxDepth: 6, managedBy: null,
    };
    setForm((f) => ({
      ...f,
      profiles: [...f.profiles, profile],
      // First profile becomes the active one — otherwise the operator has to make two choices
      // to get to a working setup.
      activeProfileId: f.activeProfileId || id,
    }));
    setSecrets((s) => ({ ...s, [id]: { mode: 'change', value: '' } }));
    setSelectedId(id);
  };

  const removeProfile = (id: string) => {
    setForm((f) => {
      const profiles = f.profiles.filter((p) => p.id !== id);
      return {
        ...f,
        profiles,
        // Dropping the active profile falls back to the first remaining one; the picker shows
        // the new selection before Save, so nothing changes behind the operator's back.
        activeProfileId: f.activeProfileId === id ? profiles[0]?.id ?? '' : f.activeProfileId,
      };
    });
    setSelectedId((current) => (current === id ? form.profiles.find((p) => p.id !== id)?.id ?? '' : current));
  };

  const buildProfilePayload = (p: LlmProfileDto) => ({
    Id: p.id,
    Name: p.name,
    BaseUrl: p.baseUrl,
    Model: p.model,
    MaxTokens: p.maxTokens,
    TimeoutSeconds: p.timeoutSeconds,
    EnableToolCalling: p.enableToolCalling,
    ToolCallMaxDepth: p.toolCallMaxDepth,
    ApiKey: serializeSecretField(secretOf(p.id).mode, secretOf(p.id).value),
  });

  const buildPayload = () => ({
    Enabled: form.enabled,
    ActiveProfileId: form.activeProfileId,
    Profiles: form.profiles.map(buildProfilePayload),
  });

  const activeProfileMissing = form.enabled
    && !form.profiles.some((p) => p.id === form.activeProfileId);

  const saveMutation = useMutation({
    mutationFn: async () => {
      setError(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<LlmDto>('Llm', buildPayload(), data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', 'Llm'], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
      // The AI entry points across the SPA gate on this — refresh so enabling/disabling
      // the LLM shows/hides them without a reload.
      refreshAiCapabilities(queryClient);
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<LlmDto>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        setError(err.body.errors.map((e) => e.message ?? JSON.stringify(e)));
        return;
      }
      setError([err instanceof Error ? err.message : String(err)]);
    },
  });

  if (isLoading || !data) {
    return <Card icon={Bot} title="LLM"><p className="text-sm">{t('adminSettings:loading')}</p></Card>;
  }

  return (
    <>
      <Card icon={Bot} title="LLM (KI)">
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <label className="flex items-center gap-2 text-sm cursor-pointer">
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={(e) => setForm({ ...form, enabled: e.target.checked })}
            disabled={isEnvLocked('Llm:Enabled')}
            className="rounded"
          />
          {t('adminSettings:enabled')}
          <EnvOverrideBadge source={data.effectiveSource['Llm:Enabled'] ?? ''} configKey="Llm:Enabled" />
        </label>

        <div className="mt-3 max-w-sm">
          <label htmlFor="llm-active-profile" className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
            {t('adminSettings:integrations.activeProfile')}
            <EnvOverrideBadge source={data.effectiveSource['Llm:ActiveProfileId'] ?? ''} configKey="Llm:ActiveProfileId" />
          </label>
          <select
            id="llm-active-profile"
            value={form.activeProfileId}
            onChange={(e) => setForm({ ...form, activeProfileId: e.target.value })}
            disabled={isEnvLocked('Llm:ActiveProfileId') || form.profiles.length === 0}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
          >
            <option value="">—</option>
            {form.profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <p className="mt-1 text-xs text-on-surface-variant">{t('adminSettings:integrations.activeProfileHint')}</p>
        </div>

        <div className="mt-4 flex items-center justify-between gap-2">
          <span className="text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
            {t('adminSettings:integrations.profiles')}
          </span>
          <button
            type="button"
            onClick={addProfile}
            className="flex items-center gap-1 px-2 py-1 text-xs text-on-surface hover:bg-surface-low rounded-md border border-outline-variant"
          >
            <Add size={12} /> {t('adminSettings:integrations.addProfile')}
          </button>
        </div>

        {form.profiles.length === 0 ? (
          <p className="mt-2 text-sm italic text-on-surface-variant">{t('adminSettings:integrations.noProfiles')}</p>
        ) : (
          // Same tab idiom as the System settings sub-nav (np-tab reads its colours from the skin
          // tokens, so it stays legible in every theme).
          <div className="np-tab-list mt-2" role="tablist" aria-label={t('adminSettings:integrations.profiles')}>
            {form.profiles.map((p) => (
              <button
                key={p.id}
                type="button"
                role="tab"
                aria-selected={p.id === selectedId}
                onClick={() => setSelectedId(p.id)}
                className={`np-tab ${p.id === selectedId ? 'is-active' : ''}`}
              >
                {p.name || p.id}
                {p.id === form.activeProfileId && (
                  <span className="inline-flex items-center gap-1 text-[10px] font-semibold uppercase tracking-wide text-green-600 dark:text-green-400">
                    <span aria-hidden="true">●</span>
                    {t('adminSettings:integrations.profileActive')}
                  </span>
                )}
                {p.managedBy && <Locked size={12} aria-hidden="true" className="text-on-surface-variant" />}
              </button>
            ))}
          </div>
        )}

        {selected && (
          <LlmProfileForm
            key={selected.id}
            profile={selected}
            secret={secretOf(selected.id)}
            effectiveSource={data.effectiveSource}
            isEnvLocked={isEnvLocked}
            hasPersistedKey={!!data.payload.profiles.find((p) => p.id === selected.id)?.apiKey}
            onPatch={(patch) => patchProfile(selected.id, patch)}
            onSecretChange={(draft) => setSecrets((s) => ({ ...s, [selected.id]: draft }))}
            onDelete={() => removeProfile(selected.id)}
            onTest={() => setShowTest(true)}
          />
        )}

        <SaveActions
          onSave={() => saveMutation.mutate()}
          saving={saveMutation.isPending}
          errors={activeProfileMissing
            ? [...(error ?? []), t('adminSettings:integrations.activeProfileRequired')]
            : error}
        />
      </Card>

      <EtagConflictDialog
        open={!!conflict}
        serverSnapshot={conflict}
        localDraft={buildPayload()}
        onKeepMine={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Llm'], conflict);
          setConflict(null);
          adminSettings.putSection<LlmDto>('Llm', buildPayload(), conflict.etag)
            .then((fresh) => {
              queryClient.setQueryData(['admin-settings', 'Llm'], fresh);
              refreshAiCapabilities(queryClient);
            })
            .catch((e: unknown) => setError([e instanceof Error ? e.message : String(e)]));
        }}
        onTakeTheirs={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Llm'], conflict);
          setConflict(null);
        }}
        onCancel={() => setConflict(null)}
      />

      <TestProbeModal
        title={t('adminSettings:testProbeTitle')}
        open={showTest}
        onClose={() => setShowTest(false)}
        runProbe={() => adminSettings.testLlm({
          // ProfileId only resolves a stored key for the "unchanged" marker; the connection under
          // test comes from the draft, so an unsaved profile can be probed too (with a typed key).
          ProfileId: selected?.id ?? null,
          Settings: {
            BaseUrl: selected?.baseUrl ?? '',
            ApiKey: selected ? serializeSecretField(secretOf(selected.id).mode, secretOf(selected.id).value) : null,
            TimeoutSeconds: selected?.timeoutSeconds ?? 90,
          },
        })}
      />
    </>
  );
}

/**
 * The editor for one profile. Split out so the card body stays readable and so remounting on
 * profile switch (via `key`) resets any uncontrolled input state.
 */
function LlmProfileForm({
  profile, secret, effectiveSource, isEnvLocked, hasPersistedKey,
  onPatch, onSecretChange, onDelete, onTest,
}: Readonly<{
  profile: LlmProfileDto;
  secret: SecretDraft;
  effectiveSource: Record<string, string>;
  isEnvLocked: (key: string) => boolean;
  hasPersistedKey: boolean;
  onPatch: (patch: Partial<LlmProfileDto>) => void;
  onSecretChange: (draft: SecretDraft) => void;
  onDelete: () => void;
  onTest: () => void;
}>) {
  const { t } = useTranslation(['adminSettings']);
  const key = (field: string) => `Llm:Profiles:${profile.id}:${field}`;

  return (
    <div className="mt-3 rounded-md border border-outline-variant bg-surface-low/40 p-3">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <LabeledInput
          label={t('adminSettings:integrations.profileName')} configKey={key('Name')} effectiveSource={effectiveSource}
          value={profile.name}
          onChange={(v) => onPatch({ name: v })}
          disabled={isEnvLocked(key('Name'))}
        />
        <LabeledInput
          label={t('adminSettings:integrations.baseUrl')} configKey={key('BaseUrl')} effectiveSource={effectiveSource}
          value={profile.baseUrl}
          onChange={(v) => onPatch({ baseUrl: v })}
          disabled={isEnvLocked(key('BaseUrl'))}
          hint={t('adminSettings:integrations.baseUrlHint')}
        />
        <LabeledInput
          label={t('adminSettings:integrations.model')} configKey={key('Model')} effectiveSource={effectiveSource}
          value={profile.model}
          onChange={(v) => onPatch({ model: v })}
          disabled={isEnvLocked(key('Model'))}
        />
        <LabeledInput
          label={t('adminSettings:integrations.maxTokens')} configKey={key('MaxTokens')} effectiveSource={effectiveSource}
          type="number" value={String(profile.maxTokens)}
          onChange={(v) => onPatch({ maxTokens: Number.parseInt(v, 10) || 0 })}
          disabled={isEnvLocked(key('MaxTokens'))}
        />
        <LabeledInput
          label={t('adminSettings:integrations.timeoutSeconds')} configKey={key('TimeoutSeconds')} effectiveSource={effectiveSource}
          type="number" value={String(profile.timeoutSeconds)}
          onChange={(v) => onPatch({ timeoutSeconds: Number.parseInt(v, 10) || 0 })}
          disabled={isEnvLocked(key('TimeoutSeconds'))}
        />
        <div className="md:col-span-2">
          <SecretField
            inputId={`llm-api-key-${profile.id}`}
            label={t('adminSettings:integrations.apiKey')}
            hasPersistedValue={hasPersistedKey}
            mode={secret.mode}
            value={secret.value}
            onModeChange={(mode) => onSecretChange({ ...secret, mode })}
            onValueChange={(value) => onSecretChange({ ...secret, value })}
            disabled={isEnvLocked(key('ApiKey'))}
          />
          <EnvOverrideBadge source={effectiveSource[key('ApiKey')] ?? ''} configKey={key('ApiKey')} />
        </div>
      </div>

      <label className="flex items-center gap-2 text-sm cursor-pointer mt-3">
        <input
          type="checkbox"
          checked={profile.enableToolCalling}
          onChange={(e) => onPatch({ enableToolCalling: e.target.checked })}
          disabled={isEnvLocked(key('EnableToolCalling'))}
          className="rounded"
        />
        {t('adminSettings:integrations.enableToolCalling')}
        <EnvOverrideBadge source={effectiveSource[key('EnableToolCalling')] ?? ''} configKey={key('EnableToolCalling')} />
      </label>
      <p className="mt-1 text-xs text-on-surface-variant">{t('adminSettings:integrations.enableToolCallingHint')}</p>

      {profile.enableToolCalling && (
        <div className="mt-3 max-w-xs">
          <LabeledInput
            label={t('adminSettings:integrations.toolCallMaxDepth')} configKey={key('ToolCallMaxDepth')} effectiveSource={effectiveSource}
            type="number" value={String(profile.toolCallMaxDepth)}
            onChange={(v) => onPatch({ toolCallMaxDepth: Number.parseInt(v, 10) || 0 })}
            disabled={isEnvLocked(key('ToolCallMaxDepth'))}
          />
        </div>
      )}

      {profile.managedBy && (
        <p className="mt-3 text-xs text-amber-700 dark:text-amber-300 flex items-center gap-1.5">
          <Locked size={12} aria-hidden="true" />
          {t('adminSettings:integrations.profileNotDeletable', { source: profile.managedBy })}
        </p>
      )}

      <div className="mt-3 flex flex-wrap gap-2 justify-end">
        <button
          type="button"
          onClick={onDelete}
          disabled={!!profile.managedBy}
          className="flex items-center gap-2 px-3 py-2 text-sm rounded-md border border-outline-variant text-error hover:bg-error-container/30 disabled:text-on-surface-variant disabled:hover:bg-transparent disabled:cursor-not-allowed"
        >
          <TrashCan size={14} /> {t('adminSettings:integrations.deleteProfile')}
        </button>
        <button
          type="button"
          onClick={onTest}
          className="flex items-center gap-2 px-3 py-2 text-sm text-on-surface hover:bg-surface-low rounded-md border border-outline-variant"
        >
          <Send size={14} /> {t('adminSettings:testButton')}
        </button>
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared UI helpers
// ─────────────────────────────────────────────────────────────────────────────

function Card({ icon: Icon, title, children }: Readonly<{ icon: React.ComponentType<{ size?: number }>; title: string; children: React.ReactNode }>) {
  return (
    <div className="np-card p-4">
      <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-4">
        <Icon size={18} /> {title}
      </h3>
      {children}
    </div>
  );
}

function LabeledInput({
  label, configKey, effectiveSource, value, onChange, type = 'text', disabled, hint,
}: Readonly<{
  label: string;
  configKey: string;
  effectiveSource: Record<string, string>;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  disabled?: boolean;
  hint?: string;
}>) {
  // The config key doubles as the field id so label and input are actually associated — several
  // of these render the same label text (e.g. one "Model" per LLM profile).
  const inputId = `setting-${configKey.replaceAll(':', '-')}`;
  return (
    <div>
      <label htmlFor={inputId} className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input
        id={inputId}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
      />
      {hint ? <p className="mt-1 text-xs text-on-surface-variant">{hint}</p> : null}
    </div>
  );
}

function SaveActions({
  onSave, onTest, saving, errors,
}: Readonly<{
  onSave: () => void;
  /** Omitted where the test button belongs to a sub-form (LLM: one probe per profile). */
  onTest?: () => void;
  saving: boolean;
  errors: string[] | null;
}>) {
  const { t } = useTranslation(['adminSettings']);
  return (
    <div className="mt-4 space-y-3">
      {errors && errors.length > 0 && (
        <div className="bg-error-container/30 border border-error/30 rounded-md p-3 text-on-error-container text-sm">
          <p className="font-semibold mb-1">{t('adminSettings:validationErrorsTitle')}</p>
          <ul className="list-disc list-inside space-y-0.5">
            {errors.map((e, i) => <li key={i}>{e}</li>)}
          </ul>
        </div>
      )}
      <div className="flex flex-wrap gap-2 justify-end">
        {onTest && (
          <button
            type="button"
            onClick={onTest}
            className="flex items-center gap-2 px-3 py-2 text-sm text-on-surface hover:bg-surface-low rounded-md border border-outline-variant"
          >
            <Send size={14} /> {t('adminSettings:testButton')}
          </button>
        )}
        <button
          type="button"
          onClick={onSave}
          disabled={saving}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-blue-600 text-white hover:bg-blue-700 disabled:bg-blue-400 rounded-md"
        >
          <Chip size={14} /> {t('adminSettings:saveButton')}
        </button>
      </div>
    </div>
  );
}
