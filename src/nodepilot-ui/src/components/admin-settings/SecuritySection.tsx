import {
  DataBase,
  Earth,
  FolderShared,
  Network_3,
  Password,
  Renew,
  Terminal,
  Webhook,
} from '@carbon/icons-react';
import { useEffect, useState } from 'react';
import { useTranslation, Trans } from 'react-i18next';
import { useMutation } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useRole } from '../../lib/rbac';
import { toast } from '../../stores/toastStore';
import { confirmDialog } from '../../stores/confirmStore';
import { SecretField, serializeSecretField, type SecretFieldMode } from './SecretField';
import { EnvOverrideBadge } from './EnvOverrideBadge';
import {
  useSectionForm,
  Card,
  HotReloadHint,
  Toggle,
  TextInput,
  StringListEditor,
  ErrorsAndSave,
} from './SectionFormHelpers';

/**
 * Security tab — seven small cards, one per top-level config root that carries
 * hardening flags. Each card has its own ETag + Save. Remote (RequireWinRmSsl +
 * WinRm timeouts + connection pool) lives under the Performance tab to keep all
 * Remote-related knobs in one place; this tab has a one-liner pointing there.
 * Plus the Admin-only secrets re-encrypt sweep (an action, not a config section).
 */
export function SecuritySection() {
  return (
    <div className="space-y-4">
      <RestApiCard />
      <FileSystemOperationCard />
      <SqlActivityCard />
      <StartProgramCard />
      <WebhookCard />
      <ExternalTriggerCard />
      <SecurityCard />
      <SecretsReencryptCard />
      <div className="bg-surface-low border border-outline-variant rounded-md p-3 text-xs text-on-surface-variant">
        <strong>Remote (WinRM):</strong> RequireWinRmSsl, WinRm timeouts und Session-Pool werden
        unter <em>Performance → Remote</em> bearbeitet — alle Remote-Knobs liegen dort
        gebündelt, damit ein Save den ganzen Block atomar überschreibt.
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// RestApi (BlockPrivateNetworks + Proxy)
// ─────────────────────────────────────────────────────────────────────────────

type RestApiDto = {
  blockPrivateNetworks: boolean;
  allowedHosts: string[];
  proxy: { enabled: boolean; address: string; bypassList: string[]; username: string | null; password: string | null };
};

function RestApiCard() {
  const { t } = useTranslation(['adminSettings', 'common']);
  const ui = useSectionForm<RestApiDto>('RestApi', {
    blockPrivateNetworks: true,
    allowedHosts: [],
    proxy: { enabled: false, address: '', bypassList: [], username: null, password: null },
  });
  const [pwMode, setPwMode] = useState<SecretFieldMode>('keep');
  const [pwValue, setPwValue] = useState('');
  useEffect(() => {
    if (ui.data) {
      setPwMode(ui.data.payload.proxy.password ? 'keep' : 'change');
      setPwValue('');
    }
  }, [ui.data]);
  if (ui.loading) return <Card icon={Network_3} title={t('sec.restApiCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  const payload = () => ({
    BlockPrivateNetworks: form.blockPrivateNetworks,
    AllowedHosts: form.allowedHosts,
    Proxy: {
      Enabled: form.proxy.enabled,
      Address: form.proxy.address,
      BypassList: form.proxy.bypassList,
      Username: form.proxy.username,
      Password: serializeSecretField(pwMode, pwValue),
    },
  });

  return (
    <Card icon={Network_3} title={t('sec.restApiCardTitle')}>
      <Toggle label={t('sec.blockPrivateNetworks')} checked={form.blockPrivateNetworks}
        onChange={(v) => set({ ...form, blockPrivateNetworks: v })}
        configKey="RestApi:BlockPrivateNetworks" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <div className="mt-3">
        <StringListEditor label={t('sec.outboundAllowedHosts')} value={form.allowedHosts}
          onChange={(v) => set({ ...form, allowedHosts: v })}
          placeholder="api.internal.example" />
        <p className="text-xs text-on-surface-variant mt-1">{t('sec.outboundAllowedHostsHint')}</p>
      </div>
      <h4 className="font-medium text-sm mt-4 mb-2">{t('sec.outboundProxy')}</h4>
      <Toggle label={t('enabled')} checked={form.proxy.enabled}
        onChange={(v) => set({ ...form, proxy: { ...form.proxy, enabled: v } })}
        configKey="RestApi:Proxy:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-2">
        <TextInput label={t('sec.proxyAddress')} value={form.proxy.address}
          onChange={(v) => set({ ...form, proxy: { ...form.proxy, address: v } })}
          configKey="RestApi:Proxy:Address" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          placeholder="http://proxy.firma.local:8080" />
        <TextInput label={t('common:username')} value={form.proxy.username ?? ''}
          onChange={(v) => set({ ...form, proxy: { ...form.proxy, username: v || null } })}
          configKey="RestApi:Proxy:Username" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <div className="md:col-span-2">
          <SecretField inputId="proxy-password" label={t('common:password')}
            hasPersistedValue={!!data.payload.proxy.password}
            mode={pwMode} value={pwValue} onModeChange={setPwMode} onValueChange={setPwValue}
            disabled={isEnvLocked('RestApi:Proxy:Password')} />
          <EnvOverrideBadge source={data.effectiveSource['RestApi:Proxy:Password'] ?? ''} configKey="RestApi:Proxy:Password" />
        </div>
        <div className="md:col-span-2">
          <StringListEditor label={t('sec.bypassList')} value={form.proxy.bypassList}
            onChange={(v) => set({ ...form, proxy: { ...form.proxy, bypassList: v } })}
            placeholder="*.firma.local" />
        </div>
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save(payload())} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// FileSystemOperation
// ─────────────────────────────────────────────────────────────────────────────

type FsoDto = { rejectTraversal: boolean; allowedRoots: string[] };

function FileSystemOperationCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<FsoDto>('FileSystemOperation', { rejectTraversal: true, allowedRoots: [] });
  if (ui.loading) return <Card icon={FolderShared} title={t('sec.fileSystemCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={FolderShared} title={t('sec.fileSystemCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <Toggle label={t('sec.rejectTraversal')} checked={form.rejectTraversal}
        onChange={(v) => set({ ...form, rejectTraversal: v })}
        configKey="FileSystemOperation:RejectTraversal" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <div className="mt-3">
        <StringListEditor label={t('sec.allowedRoots')} value={form.allowedRoots}
          onChange={(v) => set({ ...form, allowedRoots: v })} placeholder="C:\\NodePilot\\sandbox" />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ RejectTraversal: form.rejectTraversal, AllowedRoots: form.allowedRoots })} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Trivial single-toggle cards
// ─────────────────────────────────────────────────────────────────────────────

function SqlActivityCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ requireConnectionRef: boolean }>('SqlActivity', { requireConnectionRef: false });
  if (ui.loading) return <Card icon={DataBase} title={t('sec.sqlActivityCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={DataBase} title={t('sec.sqlActivityCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <Toggle label={t('sec.requireConnectionRef')}
        checked={form.requireConnectionRef}
        onChange={(v) => set({ requireConnectionRef: v })}
        configKey="SqlActivity:RequireConnectionRef" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <ErrorsAndSave errors={errors} onSave={() => save({ RequireConnectionRef: form.requireConnectionRef })} />
      {ui.dialog}
    </Card>
  );
}

function StartProgramCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ disallowShellExecute: boolean }>('StartProgram', { disallowShellExecute: true });
  if (ui.loading) return <Card icon={Terminal} title={t('sec.startProgramCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Terminal} title={t('sec.startProgramCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <Toggle label={t('sec.disallowShellExecute')} checked={form.disallowShellExecute}
        onChange={(v) => set({ disallowShellExecute: v })}
        configKey="StartProgram:DisallowShellExecute" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <ErrorsAndSave errors={errors} onSave={() => save({ DisallowShellExecute: form.disallowShellExecute })} />
      {ui.dialog}
    </Card>
  );
}

function WebhookCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ requireSecret: boolean }>('Webhook', { requireSecret: true });
  if (ui.loading) return <Card icon={Webhook} title={t('sec.webhookCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Webhook} title={t('sec.webhookCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <Toggle label={t('sec.requireWebhookSecret')} checked={form.requireSecret}
        onChange={(v) => set({ requireSecret: v })}
        configKey="Webhook:RequireSecret" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <ErrorsAndSave errors={errors} onSave={() => save({ RequireSecret: form.requireSecret })} />
      {ui.dialog}
    </Card>
  );
}

function ExternalTriggerCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ apiKey: string | null }>('ExternalTrigger', { apiKey: null });
  const [mode, setMode] = useState<SecretFieldMode>('keep');
  const [value, setValue] = useState('');
  useEffect(() => {
    if (ui.data) {
      setMode(ui.data.payload.apiKey ? 'keep' : 'change');
      setValue('');
    }
  }, [ui.data]);
  if (ui.loading) return <Card icon={Password} title={t('sec.externalTriggerCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Password} title={t('sec.externalTriggerCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <p className="text-xs text-on-surface-variant mb-2">
        <Trans i18nKey="sec.externalTriggerHint" ns="adminSettings" components={[<code key="0" />]} />
      </p>
      <SecretField inputId="external-trigger-apikey" label={t('sec.externalTriggerApiKey')}
        hasPersistedValue={!!data.payload.apiKey}
        mode={mode} value={value} onModeChange={setMode} onValueChange={setValue}
        disabled={isEnvLocked('ExternalTrigger:ApiKey')} />
      <EnvOverrideBadge source={data.effectiveSource['ExternalTrigger:ApiKey'] ?? ''} configKey="ExternalTrigger:ApiKey" />
      <ErrorsAndSave errors={errors} onSave={() => save({ ApiKey: serializeSecretField(mode, value) })} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Secrets re-encrypt sweep (POST /api/secrets/reencrypt) — an action, not a
// config section: no ETag/Save flow. Used after rotating the AES-GCM master key
// or switching Secrets:Provider (see docs/secrets-providers.md). Admin-only —
// the endpoint 403s for everyone else, so the card hides entirely.
// ─────────────────────────────────────────────────────────────────────────────

type ReencryptResult = {
  credentialsRewritten: number;
  credentialsSkipped: number;
  globalSecretsRewritten: number;
  globalSecretsSkipped: number;
  partialSuccess: boolean;
};

function SecretsReencryptCard() {
  const { t } = useTranslation('adminSettings');
  const { canAdmin } = useRole();
  const reencrypt = useMutation({
    mutationFn: () => api.post<ReencryptResult>('/secrets/reencrypt'),
    onSuccess: (r) => {
      // 207 Multi-Status (partial) also resolves — fetch treats 2xx as ok. Surface
      // partial sweeps as an error toast so skipped rows can't slip by unnoticed.
      if (r.partialSuccess) {
        toast.error(t('sec.reencryptPartial', {
          rewritten: r.credentialsRewritten + r.globalSecretsRewritten,
          skipped: r.credentialsSkipped + r.globalSecretsSkipped,
        }));
      } else {
        toast.success(t('sec.reencryptDone', {
          credentials: r.credentialsRewritten,
          globals: r.globalSecretsRewritten,
        }));
      }
    },
    onError: (err: Error) => toast.error(err.message),
  });
  if (!canAdmin) return null;

  const onClick = async () => {
    if (await confirmDialog({ message: t('sec.reencryptConfirm'), danger: true })) reencrypt.mutate();
  };

  return (
    <Card icon={Renew} title={t('sec.reencryptCardTitle')}>
      <p className="text-xs text-on-surface-variant mb-3">{t('sec.reencryptHint')}</p>
      <div className="flex justify-end">
        <button type="button" onClick={onClick} disabled={reencrypt.isPending}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-blue-600 text-white hover:bg-blue-700 rounded-md disabled:opacity-50">
          <Renew size={14} className={reencrypt.isPending ? 'animate-spin' : undefined} />
          {t('sec.reencryptButton')}
        </button>
      </div>
    </Card>
  );
}

function SecurityCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ strictAllowedHosts: boolean; allowedHosts: string }>('Security', { strictAllowedHosts: false, allowedHosts: '*' });
  if (ui.loading) return <Card icon={Earth} title={t('sec.allowedHostsCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Earth} title={t('sec.allowedHostsCardTitle')}>
      <Toggle label={t('sec.strictAllowedHosts')}
        checked={form.strictAllowedHosts}
        onChange={(v) => set({ ...form, strictAllowedHosts: v })}
        configKey="Security:StrictAllowedHosts" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <div className="mt-3">
        <TextInput label={t('sec.allowedHostsField')} value={form.allowedHosts}
          onChange={(v) => set({ ...form, allowedHosts: v })}
          configKey="AllowedHosts" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          placeholder="nodepilot.firma.local;localhost" />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ StrictAllowedHosts: form.strictAllowedHosts, AllowedHosts: form.allowedHosts })} />
      {ui.dialog}
    </Card>
  );
}
