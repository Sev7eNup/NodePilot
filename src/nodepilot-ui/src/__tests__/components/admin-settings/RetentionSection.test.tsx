import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { RetentionSection } from '../../../components/admin-settings/RetentionSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const snapshot = {
  sectionPath: 'Retention',
  payload: {
    executions: { enabled: true, maxAgeDays: 30, intervalMinutes: 60, batchSize: 500, archivePath: null },
    auditLog:   { enabled: true, maxAgeDays: 365, intervalMinutes: 720, batchSize: 1000, archivePath: null },
    workflowVersions: { enabled: true, maxVersionsPerWorkflow: 50, intervalMinutes: 1440, batchSize: 500 },
  },
  etag: '"r-1"',
  isHotReloadable: true,
  effectiveSource: {},
};

function renderSection() {
  server.use(http.get('/api/admin/settings/Retention', () => HttpResponse.json(snapshot)));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <RetentionSection />
    </QueryClientProvider>,
  );
}

describe('RetentionSection', () => {
  it('shows the hot-reload hint on all three retention cards', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    expect(screen.getAllByText(/Changes apply immediately/i).length).toBe(3);
  });

  it('renders the three sub-cards with current values', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    expect(screen.getByDisplayValue('365')).toBeInTheDocument();
    expect(screen.getByDisplayValue('50')).toBeInTheDocument();
  });

  it('Save sends PascalCase keys matching the backend DTO shape', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Retention', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...snapshot, etag: '"r-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /speichern|save/i }));

    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.Executions?.MaxAgeDays).toBe(30);
      expect(body?.AuditLog?.MaxAgeDays).toBe(365);
      expect(body?.WorkflowVersions?.MaxVersionsPerWorkflow).toBe(50);
    });
  });

  it('env-overridden retention fields are read-only and badged', async () => {
    // Regression guard for Finding 4: a v1 retention save with an EnvVar override
    // would otherwise let the operator silently overwrite the env value in the file,
    // misleading them into thinking "I saved it, it's done" while the env still wins.
    //
    // Don't use renderSection() — its embedded server.use would race the
    // env-snapshot handler and the wrong one might win. Inline-set the handler,
    // then render, so the very first GET resolves to the env-snapshot.
    const envSnapshot = {
      ...snapshot,
      effectiveSource: {
        'Retention:Executions:MaxAgeDays': 'env',
        'Retention:AuditLog:MaxAgeDays': 'cli',
      },
    };
    server.use(http.get('/api/admin/settings/Retention', () => HttpResponse.json(envSnapshot)));
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <RetentionSection />
      </QueryClientProvider>,
    );

    await waitFor(() => {
      const exec = screen.getByDisplayValue('30') as HTMLInputElement;
      expect(exec.disabled).toBe(true);
    });
    expect((screen.getByDisplayValue('365') as HTMLInputElement).disabled).toBe(true);
    // WorkflowVersions has no env override → still editable.
    expect((screen.getByDisplayValue('50') as HTMLInputElement).disabled).toBe(false);
  });

  it('400 validation errors render as a inline list', async () => {
    server.use(http.put('/api/admin/settings/Retention', () =>
      HttpResponse.json({
        code: 'SETTINGS_VALIDATION_FAILED',
        errors: [{ fields: ['Executions.MaxAgeDays'], message: 'must be between 1 and 3650' }],
      }, { status: 400 }),
    ));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /speichern|save/i }));

    await waitFor(() => expect(screen.getByText(/between 1 and 3650/i)).toBeInTheDocument());
  });
});
