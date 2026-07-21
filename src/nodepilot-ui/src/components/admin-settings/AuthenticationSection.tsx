import {
  Activity,
  Add,
  Chip,
  Earth,
  Locked,
  Password,
  Play,
  Screen,
  Security,
  TrashCan,
} from '@carbon/icons-react';
import { useEffect, useState } from 'react';
import { useTranslation, Trans } from 'react-i18next';
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

type RoleMapping = { groupSid: string; role: 'Viewer' | 'Operator' | 'Admin' };

type LdapDto = {
  enabled: boolean;
  server: string | null;
  endpoints?: string[];
  port: number;
  useSsl: boolean;
  baseDn: string | null;
  upnSuffix: string | null;
  bindTimeoutSeconds: number;
  serviceBindDn: string | null;
  servicePassword: string | null;
  allowedGroupSids?: string[];
  directorySyncIntervalMinutes?: number;
  directorySyncMaxConcurrency?: number;
  globalRoleMappings: RoleMapping[];
  jitUserDefaultRootRole: 'FolderViewer' | 'FolderOperator' | 'FolderEditor' | 'FolderAdmin' | null;
};
type WindowsDto = { enabled: boolean; allowNtlmFallback: boolean; ntlmDisabledByPolicy?: boolean };
type OidcRoleMapping = { groupId: string; role: 'Viewer' | 'Operator' | 'Admin' };
type OidcDto = {
  enabled: boolean;
  authority: string | null;
  clientId: string | null;
  clientSecret: string | null;
  displayName: string;
  nameClaimType: string;
  groupsClaimType: string;
  scopes: string[];
  allowedGroupIds: string[];
  globalRoleMappings: OidcRoleMapping[];
};
type ScimDto = {
  enabled: boolean;
  bearerToken: string | null;
  previousBearerToken: string | null;
  authority: string | null;
};
type LocalLoginMode = 'Disabled' | 'BreakGlassOnly' | 'Enabled';
type AuthDto = {
  ldap: LdapDto;
  windows: WindowsDto;
  oidc: OidcDto;
  scim: ScimDto;
  localLoginMode?: LocalLoginMode;
  sessionAbsoluteLifetimeHours?: number;
  maxAuthorizationStalenessMinutes?: number;
};

const emptyOidc = (): OidcDto => ({
  enabled: false,
  authority: null,
  clientId: null,
  clientSecret: null,
  displayName: 'Single Sign-On',
  nameClaimType: 'preferred_username',
  groupsClaimType: 'groups',
  scopes: ['openid', 'profile', 'email'],
  allowedGroupIds: [],
  globalRoleMappings: [],
});

const emptyScim = (): ScimDto => ({
  enabled: false,
  bearerToken: null,
  previousBearerToken: null,
  authority: null,
});

/**
 * Combined LDAP + Windows-Negotiate authentication tab. Two cards side by side with
 * the JIT group-role mapping table at the bottom of the LDAP card.
 *
 * The LDAP readiness probe exercises TLS trust, service bind, search-base access and
 * group resolution against the unsaved draft before an operator enables the provider.
 */
export function AuthenticationSection() {
  const { t } = useTranslation(['adminSettings']);
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState<SettingsSectionResponse<AuthDto> | null>(null);
  const [errors, setErrors] = useState<string[] | null>(null);
  const [showLdapTest, setShowLdapTest] = useState(false);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', 'Authentication'],
    queryFn: () => adminSettings.getSection<AuthDto>('Authentication'),
  });

  const [form, setForm] = useState<AuthDto>({
    ldap: {
      enabled: false, server: null, port: 636, useSsl: true,
      endpoints: [],
      baseDn: null, upnSuffix: null, bindTimeoutSeconds: 5,
      serviceBindDn: null, servicePassword: null,
      allowedGroupSids: [], directorySyncIntervalMinutes: 5,
      directorySyncMaxConcurrency: 16,
      globalRoleMappings: [], jitUserDefaultRootRole: null,
    },
    windows: { enabled: false, allowNtlmFallback: false, ntlmDisabledByPolicy: false },
    oidc: emptyOidc(),
    scim: emptyScim(),
    localLoginMode: 'BreakGlassOnly',
    sessionAbsoluteLifetimeHours: 8,
    maxAuthorizationStalenessMinutes: 15,
  });
  const [pwMode, setPwMode] = useState<SecretFieldMode>('keep');
  const [pwValue, setPwValue] = useState('');
  const [oidcSecretMode, setOidcSecretMode] = useState<SecretFieldMode>('keep');
  const [oidcSecretValue, setOidcSecretValue] = useState('');
  const [scimTokenMode, setScimTokenMode] = useState<SecretFieldMode>('keep');
  const [scimTokenValue, setScimTokenValue] = useState('');
  const [previousScimTokenMode, setPreviousScimTokenMode] = useState<SecretFieldMode>('keep');
  const [previousScimTokenValue, setPreviousScimTokenValue] = useState('');

  useEffect(() => {
    if (!data) return;
    const ldap = data.payload.ldap;
    setForm({
      ...data.payload,
      localLoginMode: data.payload.localLoginMode ?? 'BreakGlassOnly',
      sessionAbsoluteLifetimeHours: data.payload.sessionAbsoluteLifetimeHours ?? 8,
      maxAuthorizationStalenessMinutes: data.payload.maxAuthorizationStalenessMinutes ?? 15,
      ldap: {
        ...ldap,
        useSsl: true,
        endpoints: ldap.endpoints?.length ? ldap.endpoints : (ldap.server ? [ldap.server] : []),
        allowedGroupSids: ldap.allowedGroupSids ?? [],
        directorySyncIntervalMinutes: ldap.directorySyncIntervalMinutes ?? 5,
        directorySyncMaxConcurrency: ldap.directorySyncMaxConcurrency ?? 16,
      },
      windows: { ...data.payload.windows, allowNtlmFallback: false },
      oidc: {
        ...emptyOidc(),
        ...(data.payload.oidc ?? {}),
        scopes: data.payload.oidc?.scopes ?? ['openid', 'profile', 'email'],
        allowedGroupIds: data.payload.oidc?.allowedGroupIds ?? [],
        globalRoleMappings: data.payload.oidc?.globalRoleMappings ?? [],
      },
      scim: { ...emptyScim(), ...(data.payload.scim ?? {}) },
    });
    setPwMode(data.payload.ldap.servicePassword ? 'keep' : 'change');
    setPwValue('');
    setOidcSecretMode(data.payload.oidc?.clientSecret ? 'keep' : 'change');
    setOidcSecretValue('');
    setScimTokenMode(data.payload.scim?.bearerToken ? 'keep' : 'change');
    setScimTokenValue('');
    setPreviousScimTokenMode(data.payload.scim?.previousBearerToken ? 'keep' : 'change');
    setPreviousScimTokenValue('');
  }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const buildPayload = () => ({
    Ldap: {
      Enabled: form.ldap.enabled,
      Server: form.ldap.endpoints?.[0] ?? form.ldap.server,
      Endpoints: form.ldap.endpoints?.map(v => v.trim()).filter(Boolean) ?? [],
      Port: form.ldap.port,
      UseSsl: true,
      BaseDn: form.ldap.baseDn,
      UpnSuffix: form.ldap.upnSuffix,
      BindTimeoutSeconds: form.ldap.bindTimeoutSeconds,
      ServiceBindDn: form.ldap.serviceBindDn,
      ServicePassword: serializeSecretField(pwMode, pwValue),
      AllowedGroupSids: form.ldap.allowedGroupSids?.map(v => v.trim()).filter(Boolean) ?? [],
      DirectorySyncIntervalMinutes: form.ldap.directorySyncIntervalMinutes ?? 5,
      DirectorySyncMaxConcurrency: form.ldap.directorySyncMaxConcurrency ?? 16,
      GlobalRoleMappings: form.ldap.globalRoleMappings.map(m => ({ GroupSid: m.groupSid, Role: m.role })),
      JitUserDefaultRootRole: form.ldap.jitUserDefaultRootRole,
    },
    Windows: {
      Enabled: form.windows.enabled,
      AllowNtlmFallback: false,
      NtlmDisabledByPolicy: form.windows.ntlmDisabledByPolicy ?? false,
    },
    Oidc: {
      Enabled: form.oidc.enabled,
      Authority: form.oidc.authority,
      ClientId: form.oidc.clientId,
      ClientSecret: serializeSecretField(oidcSecretMode, oidcSecretValue),
      DisplayName: form.oidc.displayName,
      NameClaimType: form.oidc.nameClaimType,
      GroupsClaimType: form.oidc.groupsClaimType,
      Scopes: form.oidc.scopes.map(v => v.trim()).filter(Boolean),
      AllowedGroupIds: form.oidc.allowedGroupIds.map(v => v.trim()).filter(Boolean),
      GlobalRoleMappings: form.oidc.globalRoleMappings.map(m => ({ GroupId: m.groupId.trim(), Role: m.role })),
    },
    Scim: {
      Enabled: form.scim.enabled,
      BearerToken: serializeSecretField(scimTokenMode, scimTokenValue),
      PreviousBearerToken: serializeSecretField(previousScimTokenMode, previousScimTokenValue),
      Authority: form.scim.authority,
    },
    LocalLoginMode: form.localLoginMode ?? 'BreakGlassOnly',
    SessionAbsoluteLifetimeHours: form.sessionAbsoluteLifetimeHours ?? 8,
    MaxAuthorizationStalenessMinutes: form.maxAuthorizationStalenessMinutes ?? 15,
  });

  const saveMutation = useMutation({
    mutationFn: async () => {
      setErrors(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<AuthDto>('Authentication', buildPayload(), data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', 'Authentication'], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<AuthDto>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        setErrors(err.body.errors.map((e: any) => {
          const fields = e.fields?.length ? `${e.fields.join(', ')}: ` : '';
          return `${fields}${e.message ?? JSON.stringify(e)}`;
        }));
        return;
      }
      setErrors([err instanceof Error ? err.message : String(err)]);
    },
  });

  if (isLoading || !data) {
    return <Card icon={Locked} title={t('adminSettings:auth.cardTitle')}><p className="text-sm">{t('adminSettings:loading')}</p></Card>;
  }

  const ldap = form.ldap;
  const windows = form.windows;
  const oidc = form.oidc;
  const scim = form.scim;

  return (
    <div className="space-y-4">
      <Card icon={Locked} title="LDAP">
        <ToggleRow
          label={t('adminSettings:enabled')}
          checked={ldap.enabled}
          onChange={(v) => setForm({ ...form, ldap: { ...ldap, enabled: v } })}
          configKey="Authentication:Ldap:Enabled"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
        />

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-3">
          <div className="md:col-span-2">
            <StringListEditor
              label={t('adminSettings:auth.ldapEndpoints')}
              values={ldap.endpoints ?? []}
              onChange={(endpoints) => setForm({ ...form, ldap: { ...ldap, endpoints, server: endpoints[0] ?? null } })}
              placeholder="dc01.firma.local"
              addLabel={t('adminSettings:auth.addEndpoint')}
              configKey="Authentication:Ldap:Endpoints"
              effectiveSource={data.effectiveSource}
              disabled={isEnvLocked('Authentication:Ldap:Endpoints') || isEnvLocked('Authentication:Ldap:Server')}
            />
          </div>
          <NumberInput label="Port" configKey="Authentication:Ldap:Port"
            value={ldap.port} onChange={(v) => setForm({ ...form, ldap: { ...ldap, port: v } })}
            min={1} max={65535}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <TextInput label="BaseDn" configKey="Authentication:Ldap:BaseDn"
            value={ldap.baseDn ?? ''} onChange={(v) => setForm({ ...form, ldap: { ...ldap, baseDn: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} placeholder="DC=firma,DC=local" />
          <TextInput label={t('adminSettings:auth.upnSuffix')} configKey="Authentication:Ldap:UpnSuffix"
            value={ldap.upnSuffix ?? ''} onChange={(v) => setForm({ ...form, ldap: { ...ldap, upnSuffix: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} placeholder="firma.local" />
          <ToggleRow label={t('adminSettings:auth.requireLdaps')} checked
            onChange={() => {}}
            configKey="Authentication:Ldap:UseSsl"
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} disabled />
          <NumberInput label={t('adminSettings:auth.bindTimeout')} configKey="Authentication:Ldap:BindTimeoutSeconds"
            value={ldap.bindTimeoutSeconds} onChange={(v) => setForm({ ...form, ldap: { ...ldap, bindTimeoutSeconds: v } })}
            min={1} max={5}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <TextInput label={t('adminSettings:auth.serviceBindDn')} configKey="Authentication:Ldap:ServiceBindDn"
            value={ldap.serviceBindDn ?? ''} onChange={(v) => setForm({ ...form, ldap: { ...ldap, serviceBindDn: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} placeholder="CN=svc-ldap,OU=Services,DC=firma,DC=local" />
          <NumberInput label={t('adminSettings:auth.syncInterval')} configKey="Authentication:Ldap:DirectorySyncIntervalMinutes"
            value={ldap.directorySyncIntervalMinutes ?? 5}
            onChange={(v) => setForm({ ...form, ldap: { ...ldap, directorySyncIntervalMinutes: v } })}
            min={1} max={5}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <NumberInput label={t('adminSettings:auth.syncConcurrency')} configKey="Authentication:Ldap:DirectorySyncMaxConcurrency"
            value={ldap.directorySyncMaxConcurrency ?? 16}
            onChange={(v) => setForm({ ...form, ldap: { ...ldap, directorySyncMaxConcurrency: v } })}
            min={1} max={32}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <NumberInput label={t('adminSettings:auth.maxStaleness')} configKey="Authentication:MaxAuthorizationStalenessMinutes"
            value={form.maxAuthorizationStalenessMinutes ?? 15}
            onChange={(v) => setForm({ ...form, maxAuthorizationStalenessMinutes: v })}
            min={1} max={15}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <div className="md:col-span-2">
            <SecretField
              inputId="ldap-service-password"
              label={t('adminSettings:auth.servicePassword')}
              hasPersistedValue={!!data.payload.ldap.servicePassword}
              mode={pwMode}
              value={pwValue}
              onModeChange={setPwMode}
              onValueChange={setPwValue}
              disabled={isEnvLocked('Authentication:Ldap:ServicePassword')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Authentication:Ldap:ServicePassword'] ?? ''} configKey="Authentication:Ldap:ServicePassword" />
          </div>
          <SelectInput<'FolderViewer' | 'FolderOperator' | 'FolderEditor' | 'FolderAdmin' | ''>
            label={t('adminSettings:auth.jitDefaultRootRole')}
            value={ldap.jitUserDefaultRootRole ?? ''}
            onChange={(v) => setForm({ ...form, ldap: { ...ldap, jitUserDefaultRootRole: v === '' ? null : v } })}
            options={[
              { value: '', label: t('adminSettings:auth.jitNoAutoGrant') },
              { value: 'FolderViewer', label: t('adminSettings:auth.folderViewer') },
              { value: 'FolderOperator', label: t('adminSettings:auth.folderOperator') },
              { value: 'FolderEditor', label: t('adminSettings:auth.folderEditor') },
              { value: 'FolderAdmin', label: t('adminSettings:auth.folderAdmin') },
            ]} />
        </div>

        <hr className="my-4 border-outline-variant" />

        <StringListEditor
          label={t('adminSettings:auth.allowedGroupsTitle')}
          values={ldap.allowedGroupSids ?? []}
          onChange={(allowedGroupSids) => setForm({ ...form, ldap: { ...ldap, allowedGroupSids } })}
          placeholder="S-1-5-21-..."
          addLabel={t('adminSettings:auth.addAllowedGroup')}
          configKey="Authentication:Ldap:AllowedGroupSids"
          effectiveSource={data.effectiveSource}
          disabled={isEnvLocked('Authentication:Ldap:AllowedGroupSids')}
        />
        {ldap.enabled && (ldap.allowedGroupSids?.length ?? 0) === 0 && (
          <p className="mt-2 text-xs text-error" role="alert">{t('adminSettings:auth.allowedGroupsRequired')}</p>
        )}

        <hr className="my-4 border-outline-variant" />

        <RoleMappingsEditor
          mappings={ldap.globalRoleMappings}
          onChange={(m) => setForm({ ...form, ldap: { ...ldap, globalRoleMappings: m } })}
        />

        <div className="mt-4 flex items-center justify-between rounded-md border border-outline-variant bg-surface-low p-3">
          <div>
            <p className="text-sm font-medium flex items-center gap-2"><Activity size={14} />{t('adminSettings:auth.ldapHealth')}</p>
            <p className="text-xs text-on-surface-variant mt-0.5">{t('adminSettings:auth.ldapHealthHint')}</p>
          </div>
          <button
            type="button"
            onClick={() => setShowLdapTest(true)}
            className="flex items-center gap-1.5 px-3 py-1.5 text-sm border border-outline-variant rounded-md hover:bg-surface-high"
          >
            <Play size={13} /> {t('adminSettings:testButton')}
          </button>
        </div>
      </Card>
      <Card icon={Screen} title={t('adminSettings:auth.windowsCardTitle')}>
        <ToggleRow label={t('adminSettings:enabled')} checked={windows.enabled}
          onChange={(v) => setForm({ ...form, windows: { ...windows, enabled: v } })}
          configKey="Authentication:Windows:Enabled"
          effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <ToggleRow label={t('adminSettings:auth.requireKerberos')}
          checked
          onChange={() => {}}
          configKey="Authentication:Windows:AllowNtlmFallback"
          effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} disabled />
        <ToggleRow label={t('adminSettings:auth.ntlmDisabledByPolicy')}
          checked={windows.ntlmDisabledByPolicy ?? false}
          onChange={(v) => setForm({ ...form, windows: { ...windows, ntlmDisabledByPolicy: v } })}
          configKey="Authentication:Windows:NtlmDisabledByPolicy"
          effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        {windows.enabled && !windows.ntlmDisabledByPolicy && (
          <p className="mt-2 text-xs text-error" role="alert">{t('adminSettings:auth.ntlmPolicyRequired')}</p>
        )}
      </Card>
      <Card icon={Earth} title={t('adminSettings:auth.oidcCardTitle')}>
        <ToggleRow label={t('adminSettings:enabled')} checked={oidc.enabled}
          onChange={(v) => setForm({ ...form, oidc: { ...oidc, enabled: v } })}
          configKey="Authentication:Oidc:Enabled"
          effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <p className="mb-3 text-xs text-on-surface-variant">{t('adminSettings:auth.oidcHint')}</p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <TextInput label={t('adminSettings:auth.oidcAuthority')} configKey="Authentication:Oidc:Authority"
            value={oidc.authority ?? ''}
            onChange={(v) => setForm({ ...form, oidc: { ...oidc, authority: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
            placeholder="https://login.example.com/tenant/v2.0" />
          <TextInput label={t('adminSettings:auth.oidcDisplayName')} configKey="Authentication:Oidc:DisplayName"
            value={oidc.displayName}
            onChange={(v) => setForm({ ...form, oidc: { ...oidc, displayName: v } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <TextInput label={t('adminSettings:auth.oidcClientId')} configKey="Authentication:Oidc:ClientId"
            value={oidc.clientId ?? ''}
            onChange={(v) => setForm({ ...form, oidc: { ...oidc, clientId: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <div>
            <SecretField
              inputId="oidc-client-secret"
              label={t('adminSettings:auth.oidcClientSecret')}
              hasPersistedValue={!!data.payload.oidc?.clientSecret}
              mode={oidcSecretMode}
              value={oidcSecretValue}
              onModeChange={setOidcSecretMode}
              onValueChange={setOidcSecretValue}
              disabled={isEnvLocked('Authentication:Oidc:ClientSecret')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Authentication:Oidc:ClientSecret'] ?? ''} configKey="Authentication:Oidc:ClientSecret" />
          </div>
          <TextInput label={t('adminSettings:auth.oidcNameClaim')} configKey="Authentication:Oidc:NameClaimType"
            value={oidc.nameClaimType}
            onChange={(v) => setForm({ ...form, oidc: { ...oidc, nameClaimType: v } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <TextInput label={t('adminSettings:auth.oidcGroupsClaim')} configKey="Authentication:Oidc:GroupsClaimType"
            value={oidc.groupsClaimType}
            onChange={(v) => setForm({ ...form, oidc: { ...oidc, groupsClaimType: v } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        </div>

        <div className="mt-4 grid grid-cols-1 md:grid-cols-2 gap-4">
          <StringListEditor
            label={t('adminSettings:auth.oidcScopes')}
            values={oidc.scopes}
            onChange={(scopes) => setForm({ ...form, oidc: { ...oidc, scopes } })}
            placeholder="openid"
            addLabel={t('adminSettings:auth.addOidcScope')}
            configKey="Authentication:Oidc:Scopes"
            effectiveSource={data.effectiveSource}
            disabled={isEnvLocked('Authentication:Oidc:Scopes')}
          />
          <StringListEditor
            label={t('adminSettings:auth.oidcAllowedGroups')}
            values={oidc.allowedGroupIds}
            onChange={(allowedGroupIds) => setForm({ ...form, oidc: { ...oidc, allowedGroupIds } })}
            placeholder="group-object-id"
            addLabel={t('adminSettings:auth.addOidcGroup')}
            configKey="Authentication:Oidc:AllowedGroupIds"
            effectiveSource={data.effectiveSource}
            disabled={isEnvLocked('Authentication:Oidc:AllowedGroupIds')}
          />
        </div>
        {oidc.enabled && oidc.allowedGroupIds.length === 0 && (
          <p className="mt-2 text-xs text-error" role="alert">{t('adminSettings:auth.oidcGroupsRequired')}</p>
        )}

        <hr className="my-4 border-outline-variant" />
        <OidcRoleMappingsEditor
          mappings={oidc.globalRoleMappings}
          onChange={(globalRoleMappings) => setForm({ ...form, oidc: { ...oidc, globalRoleMappings } })}
        />
      </Card>
      <Card icon={Password} title={t('adminSettings:auth.scimCardTitle')}>
        <ToggleRow label={t('adminSettings:enabled')} checked={scim.enabled}
          onChange={(v) => setForm({ ...form, scim: { ...scim, enabled: v } })}
          configKey="Authentication:Scim:Enabled"
          effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <p className="mb-3 text-xs text-on-surface-variant">{t('adminSettings:auth.scimHint')}</p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <TextInput label={t('adminSettings:auth.scimAuthority')} configKey="Authentication:Scim:Authority"
            value={scim.authority ?? ''}
            onChange={(v) => setForm({ ...form, scim: { ...scim, authority: v || null } })}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
            placeholder={oidc.authority ?? 'https://issuer.example.com'} />
          <div>
            <SecretField
              inputId="scim-bearer-token"
              label={t('adminSettings:auth.scimBearerToken')}
              hasPersistedValue={!!data.payload.scim?.bearerToken}
              mode={scimTokenMode}
              value={scimTokenValue}
              onModeChange={setScimTokenMode}
              onValueChange={setScimTokenValue}
              disabled={isEnvLocked('Authentication:Scim:BearerToken')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Authentication:Scim:BearerToken'] ?? ''} configKey="Authentication:Scim:BearerToken" />
          </div>
          <div>
            <SecretField
              inputId="scim-previous-bearer-token"
              label={t('adminSettings:auth.scimPreviousBearerToken')}
              hasPersistedValue={!!data.payload.scim?.previousBearerToken}
              mode={previousScimTokenMode}
              value={previousScimTokenValue}
              onModeChange={setPreviousScimTokenMode}
              onValueChange={setPreviousScimTokenValue}
              disabled={isEnvLocked('Authentication:Scim:PreviousBearerToken')}
            />
            <EnvOverrideBadge source={data.effectiveSource['Authentication:Scim:PreviousBearerToken'] ?? ''} configKey="Authentication:Scim:PreviousBearerToken" />
          </div>
        </div>
        <p className="mt-3 text-xs text-on-surface-variant">
          {t('adminSettings:auth.scimEndpoint')} <code>/api/scim/v2</code>
        </p>
      </Card>
      <Card icon={Security} title={t('adminSettings:auth.localLoginTitle')}>
        <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
          {t('adminSettings:auth.localLoginMode')}
          <EnvOverrideBadge source={data.effectiveSource['Authentication:LocalLoginMode'] ?? ''} configKey="Authentication:LocalLoginMode" />
        </label>
        <select
          value={form.localLoginMode ?? 'BreakGlassOnly'}
          onChange={(e) => setForm({ ...form, localLoginMode: e.target.value as LocalLoginMode })}
          disabled={isEnvLocked('Authentication:LocalLoginMode')}
          className="w-full md:w-80 px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low"
        >
          <option value="Disabled">{t('adminSettings:auth.localDisabled')}</option>
          <option value="BreakGlassOnly">{t('adminSettings:auth.localBreakGlass')}</option>
          <option value="Enabled">{t('adminSettings:auth.localEnabled')}</option>
        </select>
        <p className="mt-2 text-xs text-on-surface-variant">{t('adminSettings:auth.localLoginHint')}</p>
        <div className="mt-4 grid grid-cols-1 md:grid-cols-2 gap-3">
          <NumberInput
            label={t('adminSettings:auth.sessionLifetime')}
            configKey="Authentication:SessionAbsoluteLifetimeHours"
            value={form.sessionAbsoluteLifetimeHours ?? 8}
            onChange={(v) => setForm({ ...form, sessionAbsoluteLifetimeHours: v })}
            min={1} max={168}
            effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          />
        </div>
      </Card>
      {errors && errors.length > 0 && (
        <div className="bg-red-50 border border-red-200 rounded-md p-3 text-red-900 text-sm">
          <p className="font-semibold mb-1">{t('adminSettings:validationErrorsTitle')}</p>
          <ul className="list-disc list-inside space-y-0.5">
            {errors.map((e, i) => <li key={i}>{e}</li>)}
          </ul>
        </div>
      )}
      <div className="flex justify-end">
        <button
          type="button"
          onClick={() => saveMutation.mutate()}
          disabled={saveMutation.isPending}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-blue-600 text-white hover:bg-blue-700 disabled:bg-blue-400 rounded-md"
        >
          <Chip size={14} /> {t('adminSettings:saveButton')}
        </button>
      </div>
      <EtagConflictDialog
        open={!!conflict}
        serverSnapshot={conflict}
        localDraft={buildPayload()}
        onKeepMine={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Authentication'], conflict);
          setConflict(null);
          adminSettings.putSection<AuthDto>('Authentication', buildPayload(), conflict.etag)
            .then((fresh) => queryClient.setQueryData(['admin-settings', 'Authentication'], fresh))
            .catch((e: unknown) => setErrors([e instanceof Error ? e.message : String(e)]));
        }}
        onTakeTheirs={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Authentication'], conflict);
          setConflict(null);
        }}
        onCancel={() => setConflict(null)}
      />
      <TestProbeModal
        title={t('adminSettings:auth.ldapTestTitle')}
        open={showLdapTest}
        onClose={() => setShowLdapTest(false)}
        runProbe={() => adminSettings.testLdap({ Settings: buildPayload().Ldap })}
      />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Sub-widgets
// ─────────────────────────────────────────────────────────────────────────────

function StringListEditor({
  label, values, onChange, placeholder, addLabel, configKey, effectiveSource, disabled,
}: Readonly<{
  label: string;
  values: string[];
  onChange: (next: string[]) => void;
  placeholder: string;
  addLabel: string;
  configKey: string;
  effectiveSource: Record<string, string>;
  disabled: boolean;
}>) {
  const { t } = useTranslation('adminSettings');
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <h4 className="font-medium text-sm flex items-center gap-2">
          {label}
          <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
        </h4>
        <button
          type="button"
          onClick={() => onChange([...values, ''])}
          disabled={disabled}
          className="flex items-center gap-1 text-xs text-blue-600 hover:bg-blue-50 px-2 py-1 rounded disabled:opacity-40"
        >
          <Add size={12} /> {addLabel}
        </button>
      </div>
      {values.length === 0 && (
        <p className="text-xs text-outline italic">{t('auth.noListEntries')}</p>
      )}
      <div className="space-y-2">
        {values.map((value, idx) => (
          <div key={`${configKey}-${idx}`} className="flex items-center gap-2">
            <input
              type="text"
              value={value}
              onChange={(e) => {
                const next = [...values];
                next[idx] = e.target.value;
                onChange(next);
              }}
              placeholder={placeholder}
              disabled={disabled}
              aria-label={`${label} ${idx + 1}`}
              className="flex-1 px-3 py-1.5 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low"
            />
            <button
              type="button"
              onClick={() => onChange(values.filter((_, i) => i !== idx))}
              disabled={disabled}
              className="p-1.5 text-red-600 hover:bg-red-50 rounded disabled:opacity-40"
              aria-label={`${t('auth.removeListEntry')} ${idx + 1}`}
            >
              <TrashCan size={14} />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function RoleMappingsEditor({
  mappings, onChange,
}: Readonly<{
  mappings: RoleMapping[];
  onChange: (next: RoleMapping[]) => void;
}>) {
  const { t } = useTranslation('adminSettings');
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <h4 className="font-medium text-sm flex items-center gap-2">
          <Security size={14} /> {t('auth.roleMappingsTitle')}
        </h4>
        <button
          type="button"
          onClick={() => onChange([...mappings, { groupSid: '', role: 'Viewer' }])}
          className="flex items-center gap-1 text-xs text-blue-600 hover:bg-blue-50 px-2 py-1 rounded"
        >
          <Add size={12} /> {t('auth.addMapping')}
        </button>
      </div>
      {mappings.length === 0 && (
        <p className="text-xs text-outline italic">
          <Trans i18nKey="auth.noMappings" ns="adminSettings" components={[<code key="0" />]} />
        </p>
      )}
      <div className="space-y-2">
        {mappings.map((m, idx) => (
          <div key={idx} className="flex items-center gap-2">
            <input
              type="text"
              value={m.groupSid}
              onChange={(e) => {
                const next = [...mappings];
                next[idx] = { ...m, groupSid: e.target.value };
                onChange(next);
              }}
              placeholder="S-1-5-21-..."
              className="flex-1 px-3 py-1.5 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <select
              value={m.role}
              onChange={(e) => {
                const next = [...mappings];
                next[idx] = { ...m, role: e.target.value as 'Viewer' | 'Operator' | 'Admin' };
                onChange(next);
              }}
              className="px-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="Viewer">Viewer</option>
              <option value="Operator">Operator</option>
              <option value="Admin">Admin</option>
            </select>
            <button
              type="button"
              onClick={() => onChange(mappings.filter((_, i) => i !== idx))}
              className="p-1.5 text-red-600 hover:bg-red-50 rounded"
              aria-label={t('auth.removeMapping')}
            >
              <TrashCan size={14} />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function OidcRoleMappingsEditor({
  mappings, onChange,
}: Readonly<{
  mappings: OidcRoleMapping[];
  onChange: (next: OidcRoleMapping[]) => void;
}>) {
  const { t } = useTranslation('adminSettings');
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <h4 className="font-medium text-sm flex items-center gap-2">
          <Security size={14} /> {t('auth.oidcRoleMappingsTitle')}
        </h4>
        <button
          type="button"
          onClick={() => onChange([...mappings, { groupId: '', role: 'Viewer' }])}
          className="flex items-center gap-1 text-xs text-blue-600 hover:bg-blue-50 px-2 py-1 rounded"
        >
          <Add size={12} /> {t('auth.addMapping')}
        </button>
      </div>
      {mappings.length === 0 && (
        <p className="text-xs text-outline italic">{t('auth.oidcNoMappings')}</p>
      )}
      <div className="space-y-2">
        {mappings.map((m, idx) => (
          <div key={`${m.groupId}-${idx}`} className="flex items-center gap-2">
            <input
              type="text"
              value={m.groupId}
              onChange={(e) => {
                const next = [...mappings];
                next[idx] = { ...m, groupId: e.target.value };
                onChange(next);
              }}
              aria-label={`${t('auth.oidcGroupId')} ${idx + 1}`}
              placeholder="group-object-id"
              className="flex-1 px-3 py-1.5 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <select
              value={m.role}
              onChange={(e) => {
                const next = [...mappings];
                next[idx] = { ...m, role: e.target.value as 'Viewer' | 'Operator' | 'Admin' };
                onChange(next);
              }}
              className="px-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="Viewer">Viewer</option>
              <option value="Operator">Operator</option>
              <option value="Admin">Admin</option>
            </select>
            <button
              type="button"
              onClick={() => onChange(mappings.filter((_, i) => i !== idx))}
              className="p-1.5 text-red-600 hover:bg-red-50 rounded"
              aria-label={t('auth.removeMapping')}
            >
              <TrashCan size={14} />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function Card({ icon: Icon, title, children }: Readonly<{ icon: React.ComponentType<{ size?: number }>; title: string; children: React.ReactNode }>) {
  return (
    <div className="np-card p-4">
      <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-3">
        <Icon size={18} /> {title}
      </h3>
      {children}
    </div>
  );
}

function ToggleRow({
  label, checked, onChange, configKey, effectiveSource, isEnvLocked, disabled = false,
}: Readonly<{
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
  configKey: string;
  effectiveSource: Record<string, string>;
  isEnvLocked: (k: string) => boolean;
  disabled?: boolean;
}>) {
  return (
    <label className="flex items-center gap-2 text-sm cursor-pointer my-1.5">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        disabled={disabled || isEnvLocked(configKey)}
        className="rounded disabled:opacity-50"
      />
      {label}
      <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
    </label>
  );
}

function TextInput({
  label, value, onChange, configKey, effectiveSource, isEnvLocked, placeholder,
}: Readonly<{
  label: string;
  value: string;
  onChange: (v: string) => void;
  configKey: string;
  effectiveSource: Record<string, string>;
  isEnvLocked: (k: string) => boolean;
  placeholder?: string;
}>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={locked}
        placeholder={placeholder}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
      />
    </div>
  );
}

function NumberInput({
  label, value, onChange, min, max, configKey, effectiveSource, isEnvLocked,
}: Readonly<{
  label: string;
  value: number;
  onChange: (v: number) => void;
  min: number;
  max: number;
  configKey: string;
  effectiveSource: Record<string, string>;
  isEnvLocked: (k: string) => boolean;
}>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input
        type="number"
        value={value}
        min={min}
        max={max}
        disabled={locked}
        onChange={(e) => onChange(Number.parseInt(e.target.value, 10) || 0)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
      />
    </div>
  );
}

function SelectInput<T extends string>({
  label, value, onChange, options,
}: Readonly<{
  label: string;
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: string }[];
}>) {
  return (
    <div>
      <label className="block text-xs font-medium text-on-surface-variant mb-1">{label}</label>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value as T)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
      >
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  );
}
