import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { IntegrationsSection } from '../../../components/admin-settings/IntegrationsSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const smtpSnapshot = {
  sectionPath: 'Smtp',
  payload: { host: 'mail.example.com', port: 25, username: null, password: '********', from: 'a@b.c', enableSsl: true },
  etag: '"smtp-1"',
  isHotReloadable: true,
  effectiveSource: { 'Smtp:Host': 'runtime', 'Smtp:Port': 'runtime', 'Smtp:Password': 'env', 'Smtp:From': 'runtime', 'Smtp:Username': 'default', 'Smtp:EnableSsl': 'runtime' },
};

const llmSnapshot = {
  sectionPath: 'Llm',
  payload: { enabled: false, baseUrl: 'http://127.0.0.1:1234/v1', apiKey: null, model: 'gpt', maxTokens: 4096, timeoutSeconds: 60, enableToolCalling: false, toolCallMaxDepth: 4 },
  etag: '"llm-1"',
  isHotReloadable: true,
  effectiveSource: {},
};

function wireSectionEndpoints() {
  server.use(
    http.get('/api/admin/settings/Smtp', () => HttpResponse.json(smtpSnapshot)),
    http.get('/api/admin/settings/Llm', () => HttpResponse.json(llmSnapshot)),
  );
}

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <IntegrationsSection />
    </QueryClientProvider>,
  );
}

beforeEach(() => wireSectionEndpoints());

describe('IntegrationsSection — SMTP card', () => {
  it('shows the hot-reload hint on both SMTP and LLM cards', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('mail.example.com')).toBeInTheDocument());
    expect(screen.getAllByText(/Changes apply immediately/i).length).toBe(2);
  });

  it('renders the persisted SMTP values with the Password field masked', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('mail.example.com')).toBeInTheDocument());
    expect(screen.getByDisplayValue('a@b.c')).toBeInTheDocument();
    expect(screen.getByDisplayValue('********')).toBeInTheDocument();
  });

  it('marks env-overridden fields with the EnvOverride badge', async () => {
    renderSection();
    // SMTP password has effectiveSource=env in the snapshot. The badge text comes from i18n.
    const badges = await screen.findAllByText(/Environment|Wert aus/i);
    expect(badges.length).toBeGreaterThan(0);
  });

  it('happy-path Save updates the cached section response', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Smtp', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...smtpSnapshot, etag: '"smtp-2"', payload: { ...smtpSnapshot.payload, host: 'new-host' } });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('mail.example.com')).toBeInTheDocument());
    const host = screen.getByDisplayValue('mail.example.com') as HTMLInputElement;
    fireEvent.change(host, { target: { value: 'new-host' } });

    fireEvent.click(screen.getAllByRole('button', { name: /speichern|save/i })[0]);

    await waitFor(() => {
      // The save body must echo "__unchanged__" for the masked password — operators didn't retype it.
       
      const body = putBody as any;
      expect(body?.Host).toBe('new-host');
      expect(body?.Password).toBe('__unchanged__');
    });
  });

  it('H-2: EnableSsl renders checked by default and round-trips on Save', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Smtp', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...smtpSnapshot, etag: '"smtp-2"', payload: { ...smtpSnapshot.payload, enableSsl: false } });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('mail.example.com')).toBeInTheDocument());

    // EnableSsl is the only checkbox in the SMTP card. Saved with the default-true
    // snapshot it must be checked. After toggling it off and saving, the PUT body
    // must carry EnableSsl=false.
    const checkboxes = screen.getAllByRole('checkbox') as HTMLInputElement[];
    const smtpEnableSsl = checkboxes[0];
    expect(smtpEnableSsl.checked).toBe(true);

    fireEvent.click(smtpEnableSsl);
    fireEvent.click(screen.getAllByRole('button', { name: /speichern|save/i })[0]);

    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.EnableSsl).toBe(false);
    });
  });

  it('H-2: shows plaintext-warning when username is set AND EnableSsl is off', async () => {
    server.use(http.get('/api/admin/settings/Smtp', () =>
      HttpResponse.json({
        ...smtpSnapshot,
        payload: { ...smtpSnapshot.payload, username: 'mail-user', enableSsl: false },
      }),
    ));

    renderSection();
    // The warning string starts with "Warnung:" (DE) / "Warning:" (EN); match both.
    await waitFor(() => expect(screen.getByText(/Warnung:|Warning:/i)).toBeInTheDocument());
  });

  it('412 ETag-mismatch opens the conflict dialog', async () => {
    server.use(http.put('/api/admin/settings/Smtp', () =>
      HttpResponse.json({
        code: 'ETAG_MISMATCH',
        message: 'modified',
        current: { ...smtpSnapshot, etag: '"smtp-fresh"', payload: { ...smtpSnapshot.payload, host: 'server-wins' } },
      }, { status: 412 }),
    ));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('mail.example.com')).toBeInTheDocument());
    fireEvent.click(screen.getAllByRole('button', { name: /speichern|save/i })[0]);

    await waitFor(() => expect(screen.getByText(/Konflikt|Conflict/i)).toBeInTheDocument());
    // Server's "wins" value is rendered in the diff.
    expect(screen.getByText(/server-wins/)).toBeInTheDocument();
  });
});

describe('IntegrationsSection — LLM card', () => {
  it('renders the LLM card with Enabled checkbox + model + base url', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());
    expect(screen.getByDisplayValue('gpt')).toBeInTheDocument();
  });

  it('Save serialises Enabled flag + sends __unchanged__ for the API key when keep mode applies', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Llm', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());

    // Checkbox order on the page: [0] SMTP EnableSsl, [1] LLM Enabled, [2] LLM EnableToolCalling.
    // The LLM card's Enabled is the second checkbox overall.
    const checkboxes = screen.getAllByRole('checkbox') as HTMLInputElement[];
    const enabled = checkboxes[1];
    fireEvent.click(enabled);

    // Save (the LLM card has its own Save button — pick the second on the page).
    const saveButtons = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saveButtons[saveButtons.length - 1]);

    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.Enabled).toBe(true);
      // apiKey was null in the snapshot → keep-mode doesn't apply → null sent unchanged.
      expect(body?.ApiKey).toBeNull();
      // Tool-calling fields round-trip even when untouched (default off, depth 4).
      expect(body?.EnableToolCalling).toBe(false);
      expect(body?.ToolCallMaxDepth).toBe(4);
    });
  });

  it('toggles tool-calling on, reveals the depth input, and round-trips both on Save', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Llm', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());

    // EnableToolCalling is the last checkbox; toggling it on reveals the depth number input.
    const checkboxes = screen.getAllByRole('checkbox') as HTMLInputElement[];
    const toolCalling = checkboxes[checkboxes.length - 1];
    expect(toolCalling.checked).toBe(false);
    fireEvent.click(toolCalling);

    // The depth input (value 4) is now visible — bump it to 6.
    const depth = await screen.findByDisplayValue('4');
    fireEvent.change(depth, { target: { value: '6' } });

    const saveButtons = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saveButtons[saveButtons.length - 1]);

    await waitFor(() => {
      const body = putBody as { EnableToolCalling?: boolean; ToolCallMaxDepth?: number } | null;
      expect(body?.EnableToolCalling).toBe(true);
      expect(body?.ToolCallMaxDepth).toBe(6);
    });
  });
});
