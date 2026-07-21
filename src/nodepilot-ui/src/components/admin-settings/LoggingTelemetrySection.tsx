import { Activity, ChartBar, Chip, Document } from '@carbon/icons-react';
import { useEffect, useState } from 'react';
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
import { HotReloadHint } from './SectionFormHelpers';

/**
 * Three independently-saveable cards in one tab: Logging / OpenTelemetry / Stats.
 * Each has its own ETag + Save flow — operators can tweak log levels without
 * touching the OTLP endpoint, and vice versa. The shared restart banner above the
 * tab consolidates the "restart required" state across all three.
 */
export function LoggingTelemetrySection() {
  return (
    <div className="space-y-4">
      <LoggingCard />
      <OpenTelemetryCard />
      <StatsCard />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────────────

const LOG_LEVELS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'];
const FORMATS = ['text', 'cmtrace', 'json', 'ecs-json'];
const SAMPLING_MODES = ['ParentBasedTraceIdRatio', 'AlwaysOn', 'AlwaysOff', 'TraceIdRatio'];
const OTLP_PROTOCOLS = ['grpc', 'http/protobuf'];

// ─────────────────────────────────────────────────────────────────────────────
// Logging card
// ─────────────────────────────────────────────────────────────────────────────

type LoggingDto = {
  format: string;
  logLevel: { default: string; aspNetCore: string; efCoreCommand: string; efCoreConnection: string; efCoreInfrastructure: string };
  stepDetail: { enabled: boolean; maxOutputChars: number };
  file: { retainedFileCountLimit: number; fileSizeLimitBytes: number; async: boolean };
  redaction: { enabled: boolean };
  supportLog: { enabled: boolean; path: string; retainedFileCountLimit: number; fileSizeLimitBytes: number; dbProjectionEnabled: boolean };
};

function LoggingCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<LoggingDto>('Logging', {
    format: 'text',
    logLevel: { default: 'Warning', aspNetCore: 'Warning', efCoreCommand: 'Warning', efCoreConnection: 'Warning', efCoreInfrastructure: 'Warning' },
    stepDetail: { enabled: false, maxOutputChars: 10000 },
    file: { retainedFileCountLimit: 7, fileSizeLimitBytes: 100 * 1024 * 1024, async: true },
    redaction: { enabled: true },
    supportLog: { enabled: true, path: '', retainedFileCountLimit: 90, fileSizeLimitBytes: 10 * 1024 * 1024, dbProjectionEnabled: true },
  });

  if (ui.loading) return <Card icon={Document} title="Logging"><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  return (
    <Card icon={Document} title="Logging">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Select label="Format" value={form.format} options={FORMATS}
          onChange={(v) => set({ ...form, format: v })}
          configKey="Logging:Format" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />

        <Toggle label={t('logging.asyncFileSink')} checked={form.file.async}
          onChange={(v) => set({ ...form, file: { ...form.file, async: v } })}
          configKey="Logging:File:Async" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('logging.retainedFiles')} value={form.file.retainedFileCountLimit} min={1} max={365}
          onChange={(v) => set({ ...form, file: { ...form.file, retainedFileCountLimit: v } })}
          configKey="Logging:File:RetainedFileCountLimit" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('logging.maxFileSizeBytes')} value={form.file.fileSizeLimitBytes} min={1024 * 1024} max={10 * 1024 * 1024 * 1024}
          onChange={(v) => set({ ...form, file: { ...form.file, fileSizeLimitBytes: v } })}
          configKey="Logging:File:FileSizeLimitBytes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <h4 className="font-medium text-sm mt-4 mb-2">{t('logging.logLevels')}</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Select label={t('logging.levelDefault')} value={form.logLevel.default} options={LOG_LEVELS}
          onChange={(v) => set({ ...form, logLevel: { ...form.logLevel, default: v } })}
          configKey="Logging:LogLevel:Default" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Select label="Microsoft.AspNetCore" value={form.logLevel.aspNetCore} options={LOG_LEVELS}
          onChange={(v) => set({ ...form, logLevel: { ...form.logLevel, aspNetCore: v } })}
          configKey="Logging:LogLevel:Microsoft.AspNetCore" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Select label="EF Core: Database.Command" value={form.logLevel.efCoreCommand} options={LOG_LEVELS}
          onChange={(v) => set({ ...form, logLevel: { ...form.logLevel, efCoreCommand: v } })}
          configKey="Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Select label="EF Core: Database.Connection" value={form.logLevel.efCoreConnection} options={LOG_LEVELS}
          onChange={(v) => set({ ...form, logLevel: { ...form.logLevel, efCoreConnection: v } })}
          configKey="Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Connection" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Select label="EF Core: Infrastructure" value={form.logLevel.efCoreInfrastructure} options={LOG_LEVELS}
          onChange={(v) => set({ ...form, logLevel: { ...form.logLevel, efCoreInfrastructure: v } })}
          configKey="Logging:LogLevel:Microsoft.EntityFrameworkCore.Infrastructure" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <h4 className="font-medium text-sm mt-4 mb-2">{t('logging.stepDetailRedaction')}</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Toggle label={t('logging.stepDetailEnabled')} checked={form.stepDetail.enabled}
          onChange={(v) => set({ ...form, stepDetail: { ...form.stepDetail, enabled: v } })}
          configKey="Logging:StepDetail:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('logging.maxOutputChars')} value={form.stepDetail.maxOutputChars} min={100} max={1_000_000}
          onChange={(v) => set({ ...form, stepDetail: { ...form.stepDetail, maxOutputChars: v } })}
          configKey="Logging:StepDetail:MaxOutputChars" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('logging.outputRedaction')} checked={form.redaction.enabled}
          onChange={(v) => set({ ...form, redaction: { enabled: v } })}
          configKey="Logging:Redaction:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <h4 className="font-medium text-sm mt-4 mb-2">Support-Log (zweiter, schlanker Sink)</h4>
      <p className="text-xs text-on-surface-variant mb-2">
        Schreibt eine schlanke Plain-Text-Datei <code>nodepilot-support-*.log</code> mit nur den
        Support-relevanten Events (User-Log-Activity, Workflow-Lifecycle, Auth-Audits,
        System-Boot). Änderungen erfordern einen API-Restart.
      </p>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Toggle label={t('logging.supportLogEnabled')} checked={form.supportLog.enabled}
          onChange={(v) => set({ ...form, supportLog: { ...form.supportLog, enabled: v } })}
          configKey="Logging:SupportLog:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label="DB-Projektion (Web-Viewer)" checked={form.supportLog.dbProjectionEnabled}
          onChange={(v) => set({ ...form, supportLog: { ...form.supportLog, dbProjectionEnabled: v } })}
          configKey="Logging:SupportLog:DbProjectionEnabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <TextInput label="Pfad (leer = neben Haupt-Log)" value={form.supportLog.path}
          onChange={(v) => set({ ...form, supportLog: { ...form.supportLog, path: v } })}
          configKey="Logging:SupportLog:Path" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          placeholder="C:\\ProgramData\\NodePilot\\logs\\nodepilot-support-.log" />
        <NumberInput label="Retention (Tage)" value={form.supportLog.retainedFileCountLimit} min={1} max={365}
          onChange={(v) => set({ ...form, supportLog: { ...form.supportLog, retainedFileCountLimit: v } })}
          configKey="Logging:SupportLog:RetainedFileCountLimit" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('logging.maxFileSizeBytes')} value={form.supportLog.fileSizeLimitBytes} min={1024 * 1024} max={1024 * 1024 * 1024}
          onChange={(v) => set({ ...form, supportLog: { ...form.supportLog, fileSizeLimitBytes: v } })}
          configKey="Logging:SupportLog:FileSizeLimitBytes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save(toPascalLogging(form))} />
      {ui.dialog}
    </Card>
  );
}

function toPascalLogging(form: LoggingDto): unknown {
  return {
    Format: form.format,
    LogLevel: {
      Default: form.logLevel.default,
      'Microsoft.AspNetCore': form.logLevel.aspNetCore,
      'Microsoft.EntityFrameworkCore.Database.Command': form.logLevel.efCoreCommand,
      'Microsoft.EntityFrameworkCore.Database.Connection': form.logLevel.efCoreConnection,
      'Microsoft.EntityFrameworkCore.Infrastructure': form.logLevel.efCoreInfrastructure,
    },
    StepDetail: { Enabled: form.stepDetail.enabled, MaxOutputChars: form.stepDetail.maxOutputChars },
    File: { RetainedFileCountLimit: form.file.retainedFileCountLimit, FileSizeLimitBytes: form.file.fileSizeLimitBytes, Async: form.file.async },
    Redaction: { Enabled: form.redaction.enabled },
    SupportLog: {
      Enabled: form.supportLog.enabled,
      Path: form.supportLog.path,
      RetainedFileCountLimit: form.supportLog.retainedFileCountLimit,
      FileSizeLimitBytes: form.supportLog.fileSizeLimitBytes,
      DbProjectionEnabled: form.supportLog.dbProjectionEnabled,
    },
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// OpenTelemetry card
// ─────────────────────────────────────────────────────────────────────────────

type OtelDto = {
  enabled: boolean;
  serviceName: string;
  environment: string;
  redactHostnames: boolean;
  metricExportIntervalSeconds: number;
  otlp: { endpoint: string; protocol: string; headers: string; browserEndpoint: string };
  sampling: { mode: string; ratio: number };
  exporters: { traces: boolean; metrics: boolean; logs: boolean; prometheusScrape: boolean; prometheusScrapeAllowAnonymous: boolean };
  traceUi: { urlTemplate: string; backendName: string };
  prometheus: { queryEndpoint: string; username: string; password: string | null; bearerToken: string | null; timeoutSeconds: number };
  grafanaBaseUrl: string;
};

function OpenTelemetryCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<OtelDto>('OpenTelemetry', {
    enabled: false, serviceName: 'nodepilot-api', environment: 'dev', redactHostnames: true,
    metricExportIntervalSeconds: 30,
    otlp: { endpoint: 'http://localhost:4317', protocol: 'grpc', headers: '', browserEndpoint: '' },
    sampling: { mode: 'ParentBasedTraceIdRatio', ratio: 1.0 },
    exporters: { traces: true, metrics: true, logs: true, prometheusScrape: false, prometheusScrapeAllowAnonymous: false },
    traceUi: { urlTemplate: '', backendName: 'Tempo' },
    prometheus: { queryEndpoint: '', username: '', password: null, bearerToken: null, timeoutSeconds: 10 },
    grafanaBaseUrl: '',
  });

  const [pwMode, setPwMode] = useState<SecretFieldMode>('keep');
  const [pwValue, setPwValue] = useState('');
  const [btMode, setBtMode] = useState<SecretFieldMode>('keep');
  const [btValue, setBtValue] = useState('');

  useEffect(() => {
    if (ui.data) {
      setPwMode(ui.data.payload.prometheus.password ? 'keep' : 'change');
      setBtMode(ui.data.payload.prometheus.bearerToken ? 'keep' : 'change');
      setPwValue(''); setBtValue('');
    }
  }, [ui.data]);

  if (ui.loading) return <Card icon={Activity} title="OpenTelemetry"><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  const buildPayload = () => ({
    Enabled: form.enabled,
    ServiceName: form.serviceName,
    Environment: form.environment,
    RedactHostnames: form.redactHostnames,
    MetricExportIntervalSeconds: form.metricExportIntervalSeconds,
    Otlp: { Endpoint: form.otlp.endpoint, Protocol: form.otlp.protocol, Headers: form.otlp.headers, BrowserEndpoint: form.otlp.browserEndpoint },
    Sampling: { Mode: form.sampling.mode, Ratio: form.sampling.ratio },
    Exporters: { ...mapPascal(form.exporters) },
    TraceUi: { UrlTemplate: form.traceUi.urlTemplate, BackendName: form.traceUi.backendName },
    GrafanaBaseUrl: form.grafanaBaseUrl,
    Prometheus: {
      QueryEndpoint: form.prometheus.queryEndpoint,
      Username: form.prometheus.username,
      Password: serializeSecretField(pwMode, pwValue),
      BearerToken: serializeSecretField(btMode, btValue),
      TimeoutSeconds: form.prometheus.timeoutSeconds,
    },
  });

  return (
    <Card icon={Activity} title="OpenTelemetry">
      <Toggle label={t('enabled')} checked={form.enabled} onChange={(v) => set({ ...form, enabled: v })}
        configKey="OpenTelemetry:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />

      <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-3">
        <TextInput label={t('otel.serviceName')} value={form.serviceName} onChange={(v) => set({ ...form, serviceName: v })}
          configKey="OpenTelemetry:ServiceName" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <TextInput label={t('otel.environment')} value={form.environment} onChange={(v) => set({ ...form, environment: v })}
          configKey="OpenTelemetry:Environment" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.redactHostnames')} checked={form.redactHostnames} onChange={(v) => set({ ...form, redactHostnames: v })}
          configKey="OpenTelemetry:RedactHostnames" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('otel.metricExportInterval')} value={form.metricExportIntervalSeconds} min={1} max={3600}
          onChange={(v) => set({ ...form, metricExportIntervalSeconds: v })}
          configKey="OpenTelemetry:MetricExportIntervalSeconds" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>

      <h4 className="font-medium text-sm mt-4 mb-2">OTLP</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <TextInput label={t('otel.endpoint')} value={form.otlp.endpoint} onChange={(v) => set({ ...form, otlp: { ...form.otlp, endpoint: v } })}
          configKey="OpenTelemetry:Otlp:Endpoint" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} placeholder="http://localhost:4317" />
        <Select label={t('otel.protocol')} value={form.otlp.protocol} options={OTLP_PROTOCOLS}
          onChange={(v) => set({ ...form, otlp: { ...form.otlp, protocol: v } })}
          configKey="OpenTelemetry:Otlp:Protocol" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <TextInput label="Headers" value={form.otlp.headers} onChange={(v) => set({ ...form, otlp: { ...form.otlp, headers: v } })}
          configKey="OpenTelemetry:Otlp:Headers" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} placeholder="x-auth=…" />
        <TextInput label={t('otel.browserEndpoint')} value={form.otlp.browserEndpoint} onChange={(v) => set({ ...form, otlp: { ...form.otlp, browserEndpoint: v } })}
          configKey="OpenTelemetry:Otlp:BrowserEndpoint" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>

      <h4 className="font-medium text-sm mt-4 mb-2">{t('otel.samplingExporters')}</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Select label={t('otel.samplingMode')} value={form.sampling.mode} options={SAMPLING_MODES}
          onChange={(v) => set({ ...form, sampling: { ...form.sampling, mode: v } })}
          configKey="OpenTelemetry:Sampling:Mode" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInputFloat label={t('otel.samplingRatio')} value={form.sampling.ratio} min={0.0} max={1.0} step={0.01}
          onChange={(v) => set({ ...form, sampling: { ...form.sampling, ratio: v } })}
          configKey="OpenTelemetry:Sampling:Ratio" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.exportTraces')} checked={form.exporters.traces}
          onChange={(v) => set({ ...form, exporters: { ...form.exporters, traces: v } })}
          configKey="OpenTelemetry:Exporters:Traces" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.exportMetrics')} checked={form.exporters.metrics}
          onChange={(v) => set({ ...form, exporters: { ...form.exporters, metrics: v } })}
          configKey="OpenTelemetry:Exporters:Metrics" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.exportLogs')} checked={form.exporters.logs}
          onChange={(v) => set({ ...form, exporters: { ...form.exporters, logs: v } })}
          configKey="OpenTelemetry:Exporters:Logs" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.prometheusScrapeEndpoint')} checked={form.exporters.prometheusScrape}
          onChange={(v) => set({ ...form, exporters: { ...form.exporters, prometheusScrape: v } })}
          configKey="OpenTelemetry:Exporters:PrometheusScrape" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('otel.allowAnonymousMetrics')} checked={form.exporters.prometheusScrapeAllowAnonymous}
          onChange={(v) => set({ ...form, exporters: { ...form.exporters, prometheusScrapeAllowAnonymous: v } })}
          configKey="OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>

      <h4 className="font-medium text-sm mt-4 mb-2">{t('otel.traceUiPrometheusQuery')}</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <TextInput label={t('otel.traceUiUrlTemplate')} value={form.traceUi.urlTemplate}
          onChange={(v) => set({ ...form, traceUi: { ...form.traceUi, urlTemplate: v } })}
          configKey="OpenTelemetry:TraceUi:UrlTemplate" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          placeholder="https://tempo/trace/{traceId}" />
        <TextInput label={t('otel.traceUiBackendName')} value={form.traceUi.backendName}
          onChange={(v) => set({ ...form, traceUi: { ...form.traceUi, backendName: v } })}
          configKey="OpenTelemetry:TraceUi:BackendName" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <TextInput label={t('otel.prometheusQueryEndpoint')} value={form.prometheus.queryEndpoint}
          onChange={(v) => set({ ...form, prometheus: { ...form.prometheus, queryEndpoint: v } })}
          configKey="OpenTelemetry:Prometheus:QueryEndpoint" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <TextInput label="Grafana Basis-URL (optionaler Drill-down)" value={form.grafanaBaseUrl}
          onChange={(v) => set({ ...form, grafanaBaseUrl: v })}
          configKey="OpenTelemetry:GrafanaBaseUrl" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          placeholder="https://grafana.example.com" />
        <TextInput label={t('otel.prometheusUsername')} value={form.prometheus.username}
          onChange={(v) => set({ ...form, prometheus: { ...form.prometheus, username: v } })}
          configKey="OpenTelemetry:Prometheus:Username" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <div className="md:col-span-2">
          <SecretField inputId="prom-password" label={t('otel.prometheusPassword')}
            hasPersistedValue={!!data.payload.prometheus.password}
            mode={pwMode} value={pwValue} onModeChange={setPwMode} onValueChange={setPwValue}
            disabled={isEnvLocked('OpenTelemetry:Prometheus:Password')} />
          <EnvOverrideBadge source={data.effectiveSource['OpenTelemetry:Prometheus:Password'] ?? ''} configKey="OpenTelemetry:Prometheus:Password" />
        </div>
        <div className="md:col-span-2">
          <SecretField inputId="prom-bearer" label={t('otel.prometheusBearerToken')}
            hasPersistedValue={!!data.payload.prometheus.bearerToken}
            mode={btMode} value={btValue} onModeChange={setBtMode} onValueChange={setBtValue}
            disabled={isEnvLocked('OpenTelemetry:Prometheus:BearerToken')} />
          <EnvOverrideBadge source={data.effectiveSource['OpenTelemetry:Prometheus:BearerToken'] ?? ''} configKey="OpenTelemetry:Prometheus:BearerToken" />
        </div>
        <NumberInput label={t('otel.prometheusTimeout')} value={form.prometheus.timeoutSeconds} min={1} max={300}
          onChange={(v) => set({ ...form, prometheus: { ...form.prometheus, timeoutSeconds: v } })}
          configKey="OpenTelemetry:Prometheus:TimeoutSeconds" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>

      <ErrorsAndSave errors={errors} onSave={() => save(buildPayload())} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Stats card
// ─────────────────────────────────────────────────────────────────────────────

type StatsDto = { refreshIntervalMinutes: number; windowDays: number };

function StatsCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<StatsDto>('Stats', { refreshIntervalMinutes: 5, windowDays: 7 });
  if (ui.loading) return <Card icon={ChartBar} title="Stats"><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  return (
    <Card icon={ChartBar} title="Stats">
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('stats.refreshInterval')} value={form.refreshIntervalMinutes} min={1} max={1440}
          onChange={(v) => set({ ...form, refreshIntervalMinutes: v })}
          configKey="Stats:RefreshIntervalMinutes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('stats.windowDays')} value={form.windowDays} min={1} max={365}
          onChange={(v) => set({ ...form, windowDays: v })}
          configKey="Stats:WindowDays" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ RefreshIntervalMinutes: form.refreshIntervalMinutes, WindowDays: form.windowDays })} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// useSectionForm — generic GET+PUT+ETag+conflict hook for the three cards above
// ─────────────────────────────────────────────────────────────────────────────

type FormUi<T> = {
  loading: boolean;
  data: SettingsSectionResponse<T>;
  form: T;
  set: (next: T) => void;
  isEnvLocked: (k: string) => boolean;
  save: (payload: unknown) => void;
  errors: string[] | null;
  dialog: React.ReactNode;
};

function useSectionForm<T>(section: string, fallback: T): FormUi<T> | { loading: true } & Partial<FormUi<T>> {
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState<SettingsSectionResponse<T> | null>(null);
  const [errors, setErrors] = useState<string[] | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', section],
    queryFn: () => adminSettings.getSection<T>(section),
  });

  const [form, setForm] = useState<T>(fallback);
  useEffect(() => { if (data) setForm(data.payload); }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const saveMutation = useMutation({
    mutationFn: async (payload: unknown) => {
      setErrors(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<T>(section, payload, data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', section], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<T>);
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
    return { loading: true };
  }

  const dialog = (
    <EtagConflictDialog
      open={!!conflict}
      serverSnapshot={conflict}
      localDraft={form}
      onKeepMine={() => {
        if (!conflict) return;
        queryClient.setQueryData(['admin-settings', section], conflict);
        setConflict(null);
        adminSettings.putSection<T>(section, form, conflict.etag)
          .then((fresh) => queryClient.setQueryData(['admin-settings', section], fresh))
          .catch((e: unknown) => setErrors([e instanceof Error ? e.message : String(e)]));
      }}
      onTakeTheirs={() => {
        if (!conflict) return;
        queryClient.setQueryData(['admin-settings', section], conflict);
        setConflict(null);
      }}
      onCancel={() => setConflict(null)}
    />
  );

  return {
    loading: false,
    data,
    form,
    set: setForm,
    isEnvLocked,
    save: (payload: unknown) => saveMutation.mutate(payload),
    errors,
    dialog,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared UI widgets (kept local to this section; could be hoisted later if reused)
// ─────────────────────────────────────────────────────────────────────────────

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

function ErrorsAndSave({ errors, onSave }: Readonly<{ errors: string[] | null; onSave: () => void }>) {
  const { t } = useTranslation(['adminSettings']);
  return (
    <div className="mt-4 space-y-3">
      {errors && errors.length > 0 && (
        <div className="bg-red-50 border border-red-200 rounded-md p-3 text-red-900 text-sm">
          <p className="font-semibold mb-1">{t('adminSettings:validationErrorsTitle')}</p>
          <ul className="list-disc list-inside space-y-0.5">{errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
        </div>
      )}
      <div className="flex justify-end">
        <button type="button" onClick={onSave}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-blue-600 text-white hover:bg-blue-700 rounded-md">
          <Chip size={14} /> {t('adminSettings:saveButton')}
        </button>
      </div>
    </div>
  );
}

function Toggle({
  label, checked, onChange, configKey, effectiveSource, isEnvLocked,
}: Readonly<{ label: string; checked: boolean; onChange: (v: boolean) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean }>) {
  return (
    <label className="flex items-center gap-2 text-sm cursor-pointer">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)}
        disabled={isEnvLocked(configKey)} className="rounded disabled:opacity-50" />
      {label}
      <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
    </label>
  );
}

function TextInput({
  label, value, onChange, configKey, effectiveSource, isEnvLocked, placeholder,
}: Readonly<{ label: string; value: string; onChange: (v: string) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean; placeholder?: string }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="text" value={value} onChange={(e) => onChange(e.target.value)} disabled={locked}
        placeholder={placeholder}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
    </div>
  );
}

function NumberInput({
  label, value, onChange, min, max, configKey, effectiveSource, isEnvLocked,
}: Readonly<{ label: string; value: number; onChange: (v: number) => void; min: number; max: number; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="number" value={value} min={min} max={max} disabled={locked}
        onChange={(e) => onChange(Number.parseInt(e.target.value, 10) || 0)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
    </div>
  );
}

function NumberInputFloat({
  label, value, onChange, min, max, step, configKey, effectiveSource, isEnvLocked,
}: Readonly<{ label: string; value: number; onChange: (v: number) => void; min: number; max: number; step?: number; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="number" value={value} min={min} max={max} step={step ?? 0.1} disabled={locked}
        onChange={(e) => onChange(Number.parseFloat(e.target.value) || 0)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
    </div>
  );
}

function Select({
  label, value, options, onChange, configKey, effectiveSource, isEnvLocked,
}: Readonly<{ label: string; value: string; options: string[]; onChange: (v: string) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <select value={value} onChange={(e) => onChange(e.target.value)} disabled={locked}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant">
        {options.map(o => <option key={o} value={o}>{o}</option>)}
      </select>
    </div>
  );
}

// camelCase (UI) → PascalCase (backend DTO) key map for simple flat objects.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function mapPascal(obj: Record<string, any>): Record<string, any> {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const out: Record<string, any> = {};
  for (const [k, v] of Object.entries(obj)) out[k[0].toUpperCase() + k.slice(1)] = v;
  return out;
}
