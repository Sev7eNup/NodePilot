import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { LoggingTelemetrySection } from '../../../components/admin-settings/LoggingTelemetrySection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const loggingSnapshot = {
  sectionPath: 'Logging',
  payload: {
    format: 'cmtrace',
    logLevel: { default: 'Warning', aspNetCore: 'Warning', efCoreCommand: 'Warning', efCoreConnection: 'Warning', efCoreInfrastructure: 'Warning' },
    stepDetail: { enabled: false, maxOutputChars: 10000 },
    file: { retainedFileCountLimit: 7, fileSizeLimitBytes: 100 * 1024 * 1024, async: true },
    // The component reads form.supportLog.{enabled,dbProjectionEnabled,path} unconditionally,
    // so the fixture must carry that shape or rendering throws on an undefined property.
    supportLog: { enabled: true, dbProjectionEnabled: true, path: '', retainedFileCountLimit: 30, fileSizeLimitBytes: 50 * 1024 * 1024 },
    redaction: { enabled: true },
  },
  etag: '"log-1"', isHotReloadable: false, effectiveSource: {},
};
const otelSnapshot = {
  sectionPath: 'OpenTelemetry',
  payload: {
    enabled: false, serviceName: 'nodepilot-api', environment: 'dev', redactHostnames: true,
    metricExportIntervalSeconds: 30,
    otlp: { endpoint: 'http://localhost:4317', protocol: 'grpc', headers: '********', browserEndpoint: '' },
    sampling: { mode: 'ParentBasedTraceIdRatio', ratio: 1.0 },
    exporters: { traces: true, metrics: true, logs: true, prometheusScrape: false, prometheusScrapeAllowAnonymous: false },
    traceUi: { urlTemplate: '', backendName: 'Tempo' },
    prometheus: { queryEndpoint: '', username: '', password: null, bearerToken: null, timeoutSeconds: 10 },
  },
  etag: '"otel-1"', isHotReloadable: false, effectiveSource: {},
};
const statsSnapshot = {
  sectionPath: 'Stats',
  payload: { refreshIntervalMinutes: 5, windowDays: 7 },
  etag: '"stats-1"', isHotReloadable: true, effectiveSource: {},
};

function renderAll() {
  server.use(
    http.get('/api/admin/settings/Logging', () => HttpResponse.json(loggingSnapshot)),
    http.get('/api/admin/settings/OpenTelemetry', () => HttpResponse.json(otelSnapshot)),
    http.get('/api/admin/settings/Stats', () => HttpResponse.json(statsSnapshot)),
  );
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><LoggingTelemetrySection /></QueryClientProvider>);
}

describe('LoggingTelemetrySection', () => {
  it('shows the hot-reload hint only on the Stats card (Logging/OTel stay restart-pflichtig)', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('cmtrace')).toBeInTheDocument());
    expect(screen.getAllByText(/Changes apply immediately/i).length).toBe(1);
  });

  it('renders three cards: Logging + OpenTelemetry + Stats', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('cmtrace')).toBeInTheDocument());
    expect(screen.getByDisplayValue('nodepilot-api')).toBeInTheDocument();
    expect(screen.getByDisplayValue('http://localhost:4317')).toBeInTheDocument();
    // Several inputs share the value '7' (Logging.File.RetainedFileCountLimit and
    // Stats.WindowDays) or '5' (Stats.RefreshIntervalMinutes and Logging.LogLevels), so those
    // are matched by their min/max attributes instead: only Stats.WindowDays has max=365.
    expect(screen.getByDisplayValue('Tempo')).toBeInTheDocument();
    expect(screen.getByDisplayValue('dev')).toBeInTheDocument();
  });

  it('Stats save fires its own PUT with PascalCase payload', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Stats', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...statsSnapshot, etag: '"stats-2"' });
    }));
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('5')).toBeInTheDocument());
    // There is one save button per card; the last one belongs to Stats.
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saves[saves.length - 1]);
    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.RefreshIntervalMinutes).toBe(5);
      expect(body?.WindowDays).toBe(7);
    });
  });

  it('keeps persisted OTLP headers without echoing the masked display value', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/OpenTelemetry', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...otelSnapshot, etag: '"otel-2"' });
    }));

    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('********')).toBeInTheDocument());
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saves[1]);

    await waitFor(() => {
      const body = putBody as any;
      expect(body?.Otlp?.Headers).toBe('__unchanged__');
    });
  });

  it('rotates persisted OTLP headers through the secret-field flow', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/OpenTelemetry', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...otelSnapshot, etag: '"otel-2"' });
    }));

    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('********')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /set new value|neu setzen/i }));
    fireEvent.change(screen.getByLabelText(/OTLP.*headers/i), {
      target: { value: 'x-api-key=rotated-collector-key' },
    });
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saves[1]);

    await waitFor(() => {
      const body = putBody as any;
      expect(body?.Otlp?.Headers).toBe('x-api-key=rotated-collector-key');
    });
  });

  it('clears persisted OTLP headers explicitly', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/OpenTelemetry', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...otelSnapshot, etag: '"otel-2"' });
    }));

    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('********')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /clear|löschen/i }));
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saves[1]);

    await waitFor(() => {
      const body = putBody as any;
      expect(body?.Otlp?.Headers).toBeNull();
    });
  });

  it('preserves an OTLP header rotation when retrying an ETag conflict', async () => {
    const putBodies: unknown[] = [];
    let calls = 0;
    server.use(http.put('/api/admin/settings/OpenTelemetry', async ({ request }) => {
      putBodies.push(await request.json());
      calls += 1;
      if (calls === 1) {
        return HttpResponse.json({
          code: 'ETAG_MISMATCH',
          current: { ...otelSnapshot, etag: '"otel-current"' },
        }, { status: 412 });
      }
      return HttpResponse.json({ ...otelSnapshot, etag: '"otel-2"' });
    }));

    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('********')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /set new value|neu setzen/i }));
    fireEvent.change(screen.getByLabelText(/OTLP.*headers/i), {
      target: { value: 'x-api-key=rotated-after-conflict' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: /speichern|save/i })[1]);
    await waitFor(() => expect(screen.getByText(/Konflikt|Conflict/i)).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /overwrite with my values|meine werte überschreiben/i }));

    await waitFor(() => expect(putBodies).toHaveLength(2));
    expect((putBodies[1] as any)?.Otlp?.Headers).toBe('x-api-key=rotated-after-conflict');
  });

  it('preserves an OTLP header deletion when retrying an ETag conflict', async () => {
    const putBodies: unknown[] = [];
    let calls = 0;
    server.use(http.put('/api/admin/settings/OpenTelemetry', async ({ request }) => {
      putBodies.push(await request.json());
      calls += 1;
      if (calls === 1) {
        return HttpResponse.json({
          code: 'ETAG_MISMATCH',
          current: { ...otelSnapshot, etag: '"otel-current"' },
        }, { status: 412 });
      }
      return HttpResponse.json({ ...otelSnapshot, etag: '"otel-2"' });
    }));

    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('********')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /clear|löschen/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /speichern|save/i })[1]);
    await waitFor(() => expect(screen.getByText(/Konflikt|Conflict/i)).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /overwrite with my values|meine werte überschreiben/i }));

    await waitFor(() => expect(putBodies).toHaveLength(2));
    expect((putBodies[1] as any)?.Otlp?.Headers).toBeNull();
  });
});
