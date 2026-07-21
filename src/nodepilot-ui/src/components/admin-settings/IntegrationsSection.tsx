import { Bot, Chip, Email, Send } from '@carbon/icons-react';
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

type SmtpDto = {
  host: string;
  port: number;
  username: string | null;
  password: string | null;
  from: string;
  enableSsl: boolean;
};

type LlmDto = {
  enabled: boolean;
  baseUrl: string;
  apiKey: string | null;
  model: string;
  maxTokens: number;
  timeoutSeconds: number;
  enableToolCalling: boolean;
  toolCallMaxDepth: number;
};

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
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        setError(err.body.errors.map((e: any) => e.message ?? JSON.stringify(e)));
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

  const [form, setForm] = useState<LlmDto>({
    enabled: false, baseUrl: '', apiKey: null, model: '', maxTokens: 4096, timeoutSeconds: 90,
    enableToolCalling: false, toolCallMaxDepth: 4,
  });
  const [keyMode, setKeyMode] = useState<SecretFieldMode>('keep');
  const [keyValue, setKeyValue] = useState('');

  useEffect(() => {
    if (!data) return;
    setForm(data.payload);
    setKeyMode(data.payload.apiKey ? 'keep' : 'change');
    setKeyValue('');
  }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const buildPayload = () => ({
    Enabled: form.enabled,
    BaseUrl: form.baseUrl,
    Model: form.model,
    MaxTokens: form.maxTokens,
    TimeoutSeconds: form.timeoutSeconds,
    EnableToolCalling: form.enableToolCalling,
    ToolCallMaxDepth: form.toolCallMaxDepth,
    ApiKey: serializeSecretField(keyMode, keyValue),
  });

  const saveMutation = useMutation({
    mutationFn: async () => {
      setError(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<LlmDto>('Llm', buildPayload(), data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', 'Llm'], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<LlmDto>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        setError(err.body.errors.map((e: any) => e.message ?? JSON.stringify(e)));
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

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mt-3">
          <LabeledInput
            label="Base URL" configKey="Llm:BaseUrl" effectiveSource={data.effectiveSource}
            value={form.baseUrl}
            onChange={(v) => setForm({ ...form, baseUrl: v })}
            disabled={isEnvLocked('Llm:BaseUrl')}
          />
          <LabeledInput
            label={t('adminSettings:integrations.model')} configKey="Llm:Model" effectiveSource={data.effectiveSource}
            value={form.model}
            onChange={(v) => setForm({ ...form, model: v })}
            disabled={isEnvLocked('Llm:Model')}
          />
          <LabeledInput
            label={t('adminSettings:integrations.maxTokens')} configKey="Llm:MaxTokens" effectiveSource={data.effectiveSource}
            type="number" value={String(form.maxTokens)}
            onChange={(v) => setForm({ ...form, maxTokens: Number.parseInt(v, 10) || 0 })}
            disabled={isEnvLocked('Llm:MaxTokens')}
          />
          <LabeledInput
            label={t('adminSettings:integrations.timeoutSeconds')} configKey="Llm:TimeoutSeconds" effectiveSource={data.effectiveSource}
            type="number" value={String(form.timeoutSeconds)}
            onChange={(v) => setForm({ ...form, timeoutSeconds: Number.parseInt(v, 10) || 0 })}
            disabled={isEnvLocked('Llm:TimeoutSeconds')}
          />
          <div className="md:col-span-2">
            <SecretField
              inputId="llm-api-key"
              label={t('adminSettings:integrations.apiKey')}
              hasPersistedValue={!!data.payload.apiKey}
              mode={keyMode}
              value={keyValue}
              onModeChange={setKeyMode}
              onValueChange={setKeyValue}
              disabled={isEnvLocked('Llm:ApiKey')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Llm:ApiKey'] ?? ''} configKey="Llm:ApiKey" />
          </div>
        </div>

        <label className="flex items-center gap-2 text-sm cursor-pointer mt-3">
          <input
            type="checkbox"
            checked={form.enableToolCalling}
            onChange={(e) => setForm({ ...form, enableToolCalling: e.target.checked })}
            disabled={isEnvLocked('Llm:EnableToolCalling')}
            className="rounded"
          />
          {t('adminSettings:integrations.enableToolCalling')}
          <EnvOverrideBadge source={data.effectiveSource['Llm:EnableToolCalling'] ?? ''} configKey="Llm:EnableToolCalling" />
        </label>
        <p className="mt-1 text-xs text-on-surface-variant">{t('adminSettings:integrations.enableToolCallingHint')}</p>

        {form.enableToolCalling && (
          <div className="mt-3 max-w-xs">
            <LabeledInput
              label={t('adminSettings:integrations.toolCallMaxDepth')} configKey="Llm:ToolCallMaxDepth" effectiveSource={data.effectiveSource}
              type="number" value={String(form.toolCallMaxDepth)}
              onChange={(v) => setForm({ ...form, toolCallMaxDepth: Number.parseInt(v, 10) || 0 })}
              disabled={isEnvLocked('Llm:ToolCallMaxDepth')}
            />
          </div>
        )}

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
          queryClient.setQueryData(['admin-settings', 'Llm'], conflict);
          setConflict(null);
          adminSettings.putSection<LlmDto>('Llm', buildPayload(), conflict.etag)
            .then((fresh) => queryClient.setQueryData(['admin-settings', 'Llm'], fresh))
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
        runProbe={() => adminSettings.testLlm({ Settings: { ...buildPayload() } })}
      />
    </>
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
  label, configKey, effectiveSource, value, onChange, type = 'text', disabled,
}: Readonly<{
  label: string;
  configKey: string;
  effectiveSource: Record<string, string>;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  disabled?: boolean;
}>) {
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
      />
    </div>
  );
}

function SaveActions({
  onSave, onTest, saving, errors,
}: Readonly<{
  onSave: () => void;
  onTest: () => void;
  saving: boolean;
  errors: string[] | null;
}>) {
  const { t } = useTranslation(['adminSettings']);
  return (
    <div className="mt-4 space-y-3">
      {errors && errors.length > 0 && (
        <div className="bg-red-50 border border-red-200 rounded-md p-3 text-red-900 text-sm">
          <p className="font-semibold mb-1">{t('adminSettings:validationErrorsTitle')}</p>
          <ul className="list-disc list-inside space-y-0.5">
            {errors.map((e, i) => <li key={i}>{e}</li>)}
          </ul>
        </div>
      )}
      <div className="flex flex-wrap gap-2 justify-end">
        <button
          type="button"
          onClick={onTest}
          className="flex items-center gap-2 px-3 py-2 text-sm text-on-surface hover:bg-surface-low rounded-md border border-outline-variant"
        >
          <Send size={14} /> {t('adminSettings:testButton')}
        </button>
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
