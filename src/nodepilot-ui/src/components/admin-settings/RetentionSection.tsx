import { Chip, DataBase, Document, History } from '@carbon/icons-react';
import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  adminSettings,
  SettingsApiError,
  type SettingsSectionResponse,
} from '../../api/adminSettings';
import { EtagConflictDialog } from './EtagConflictDialog';
import { EnvOverrideBadge } from './EnvOverrideBadge';
import { HotReloadHint } from './SectionFormHelpers';

type ExecutionsDto = { enabled: boolean; maxAgeDays: number; intervalMinutes: number; batchSize: number; archivePath: string | null };
type AuditLogDto   = { enabled: boolean; maxAgeDays: number; intervalMinutes: number; batchSize: number; archivePath: string | null };
type VersionsDto   = { enabled: boolean; maxVersionsPerWorkflow: number; intervalMinutes: number; batchSize: number };

type RetentionDto = {
  executions: ExecutionsDto;
  auditLog:   AuditLogDto;
  workflowVersions: VersionsDto;
};

/**
 * Retention tab — three cards (Executions / AuditLog / WorkflowVersions). No secret
 * fields, so the form is a thin wrapper around the existing ETag/save flow. Validation
 * is server-side; field-level Range errors come back as a 400 and render inline.
 */
export function RetentionSection() {
  const { t } = useTranslation(['adminSettings']);
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState<SettingsSectionResponse<RetentionDto> | null>(null);
  const [errors, setErrors] = useState<string[] | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', 'Retention'],
    queryFn: () => adminSettings.getSection<RetentionDto>('Retention'),
  });

  const [form, setForm] = useState<RetentionDto>({
    executions: { enabled: true, maxAgeDays: 30, intervalMinutes: 60, batchSize: 500, archivePath: null },
    auditLog:   { enabled: true, maxAgeDays: 365, intervalMinutes: 720, batchSize: 1000, archivePath: null },
    workflowVersions: { enabled: true, maxVersionsPerWorkflow: 50, intervalMinutes: 1440, batchSize: 500 },
  });

  useEffect(() => {
    if (data) setForm(data.payload);
  }, [data]);

  const buildPayload = () => ({
    Executions: { ...mapKeysToPascal(form.executions) },
    AuditLog:   { ...mapKeysToPascal(form.auditLog) },
    WorkflowVersions: { ...mapKeysToPascal(form.workflowVersions) },
  });

  const saveMutation = useMutation({
    mutationFn: async () => {
      setErrors(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<RetentionDto>('Retention', buildPayload(), data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', 'Retention'], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<RetentionDto>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        setErrors(err.body.errors.map((e: any) => {
          const fieldHint = e.fields?.length ? `${e.fields.join(', ')}: ` : '';
          return `${fieldHint}${e.message ?? JSON.stringify(e)}`;
        }));
        return;
      }
      setErrors([err instanceof Error ? err.message : String(err)]);
    },
  });

  if (isLoading || !data) {
    return <Card icon={DataBase} title="Retention"><p className="text-sm">{t('adminSettings:loading')}</p></Card>;
  }

  // env/cli-overridden fields are read-only — this mirrors the SMTP/LLM cards and
  // satisfies the design contract that any field surfaced through Settings must show
  // its EnvOverrideBadge + disable when a higher-priority source wins (Finding 4).
  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  return (
    <div className="space-y-4">
      <Card icon={DataBase} title="Executions">
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <ExecutionsForm
          dto={form.executions}
          onChange={(executions) => setForm({ ...form, executions })}
          withArchivePath
          configKeyPrefix="Retention:Executions"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
        />
      </Card>
      <Card icon={Document} title={t('adminSettings:retention.auditLogCardTitle')}>
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <ExecutionsForm
          dto={form.auditLog}
          onChange={(auditLog) => setForm({ ...form, auditLog: auditLog as AuditLogDto })}
          withArchivePath
          configKeyPrefix="Retention:AuditLog"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
        />
      </Card>
      <Card icon={History} title={t('adminSettings:retention.workflowVersionsCardTitle')}>
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <VersionsForm
          dto={form.workflowVersions}
          onChange={(workflowVersions) => setForm({ ...form, workflowVersions })}
          configKeyPrefix="Retention:WorkflowVersions"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
        />
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
          queryClient.setQueryData(['admin-settings', 'Retention'], conflict);
          setConflict(null);
          adminSettings.putSection<RetentionDto>('Retention', buildPayload(), conflict.etag)
            .then((fresh) => queryClient.setQueryData(['admin-settings', 'Retention'], fresh))
            .catch((e: unknown) => setErrors([e instanceof Error ? e.message : String(e)]));
        }}
        onTakeTheirs={() => {
          if (!conflict) return;
          queryClient.setQueryData(['admin-settings', 'Retention'], conflict);
          setConflict(null);
        }}
        onCancel={() => setConflict(null)}
      />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Sub-forms
// ─────────────────────────────────────────────────────────────────────────────

type SubFormProps<TDto> = {
  dto: TDto;
  onChange: (next: TDto) => void;
  configKeyPrefix: string;
  effectiveSource: Record<string, string>;
  isEnvLocked: (key: string) => boolean;
};

function ExecutionsForm({
  dto, onChange, withArchivePath, configKeyPrefix, effectiveSource, isEnvLocked,
}: SubFormProps<{ enabled: boolean; maxAgeDays: number; intervalMinutes: number; batchSize: number; archivePath: string | null }>
  & { withArchivePath?: boolean }) {
  const { t } = useTranslation('adminSettings');
  return (
    <div className="space-y-3">
      <label className="flex items-center gap-2 text-sm cursor-pointer">
        <input
          type="checkbox"
          checked={dto.enabled}
          onChange={(e) => onChange({ ...dto, enabled: e.target.checked })}
          disabled={isEnvLocked(`${configKeyPrefix}:Enabled`)}
          className="rounded disabled:opacity-50"
        />
        {t('enabled')}
        <EnvOverrideBadge source={effectiveSource[`${configKeyPrefix}:Enabled`] ?? ''} configKey={`${configKeyPrefix}:Enabled`} />
      </label>
      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <NumberInput label={t('retention.maxAgeDays')} value={dto.maxAgeDays}
          onChange={(v) => onChange({ ...dto, maxAgeDays: v })} min={1} max={3650}
          configKey={`${configKeyPrefix}:MaxAgeDays`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('retention.intervalMinutes')} value={dto.intervalMinutes}
          onChange={(v) => onChange({ ...dto, intervalMinutes: v })} min={1} max={1440}
          configKey={`${configKeyPrefix}:IntervalMinutes`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('retention.batchSize')} value={dto.batchSize}
          onChange={(v) => onChange({ ...dto, batchSize: v })} min={1} max={10000}
          configKey={`${configKeyPrefix}:BatchSize`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      {withArchivePath && (
        <div>
          <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
            {t('retention.archivePath')}
            <EnvOverrideBadge source={effectiveSource[`${configKeyPrefix}:ArchivePath`] ?? ''} configKey={`${configKeyPrefix}:ArchivePath`} />
          </label>
          <input
            type="text"
            value={dto.archivePath ?? ''}
            onChange={(e) => onChange({ ...dto, archivePath: e.target.value || null })}
            disabled={isEnvLocked(`${configKeyPrefix}:ArchivePath`)}
            placeholder="C:\\NodePilot\\archive"
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant"
          />
        </div>
      )}
    </div>
  );
}

function VersionsForm({
  dto, onChange, configKeyPrefix, effectiveSource, isEnvLocked,
}: Readonly<SubFormProps<VersionsDto>>) {
  const { t } = useTranslation('adminSettings');
  return (
    <div className="space-y-3">
      <label className="flex items-center gap-2 text-sm cursor-pointer">
        <input
          type="checkbox"
          checked={dto.enabled}
          onChange={(e) => onChange({ ...dto, enabled: e.target.checked })}
          disabled={isEnvLocked(`${configKeyPrefix}:Enabled`)}
          className="rounded disabled:opacity-50"
        />
        {t('enabled')}
        <EnvOverrideBadge source={effectiveSource[`${configKeyPrefix}:Enabled`] ?? ''} configKey={`${configKeyPrefix}:Enabled`} />
      </label>
      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <NumberInput label={t('retention.maxVersionsPerWorkflow')} value={dto.maxVersionsPerWorkflow}
          onChange={(v) => onChange({ ...dto, maxVersionsPerWorkflow: v })} min={1} max={10000}
          configKey={`${configKeyPrefix}:MaxVersionsPerWorkflow`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('retention.intervalMinutes')} value={dto.intervalMinutes}
          onChange={(v) => onChange({ ...dto, intervalMinutes: v })} min={1} max={1440}
          configKey={`${configKeyPrefix}:IntervalMinutes`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('retention.batchSize')} value={dto.batchSize}
          onChange={(v) => onChange({ ...dto, batchSize: v })} min={1} max={10000}
          configKey={`${configKeyPrefix}:BatchSize`} effectiveSource={effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
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
  isEnvLocked: (key: string) => boolean;
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

// camelCase (UI) → PascalCase (backend DTO) key map. Backend deserialises
// case-insensitively, but TypeScript wants the exact shape on the wire so
// the test assertions and reader-side typings stay in sync.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function mapKeysToPascal(obj: Record<string, any>): Record<string, any> {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const out: Record<string, any> = {};
  for (const [k, v] of Object.entries(obj)) {
    out[k[0].toUpperCase() + k.slice(1)] = v;
  }
  return out;
}
