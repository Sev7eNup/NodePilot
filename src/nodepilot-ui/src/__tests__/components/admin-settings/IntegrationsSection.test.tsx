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

const llmProfile = (over: Record<string, unknown> = {}) => ({
  id: 'openai', name: 'OpenAI', baseUrl: 'http://127.0.0.1:1234/v1', apiKey: null, model: 'gpt',
  maxTokens: 4096, timeoutSeconds: 60, enableToolCalling: false, toolCallMaxDepth: 4, managedBy: null,
  ...over,
});

const llmSnapshot = {
  sectionPath: 'Llm',
  payload: { enabled: false, activeProfileId: 'openai', profiles: [llmProfile()] },
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
  return {
    qc,
    ...render(
      <QueryClientProvider client={qc}>
        <IntegrationsSection />
      </QueryClientProvider>,
    ),
  };
}

/** The LLM card's Save is the last one on the page (SMTP renders first). */
function clickLlmSave() {
  const buttons = screen.getAllByRole('button', { name: /speichern|save/i });
  fireEvent.click(buttons[buttons.length - 1]);
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
    const smtpEnableSsl = screen.getAllByRole('checkbox')[0] as HTMLInputElement;
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
  it('renders the active-profile picker plus the selected profile form', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());
    expect(screen.getByDisplayValue('gpt')).toBeInTheDocument();
    expect((screen.getByLabelText(/^Name$/) as HTMLInputElement).value).toBe('OpenAI');
    const picker = screen.getByLabelText(/Aktives Profil|Active profile/i) as HTMLSelectElement;
    expect(picker.value).toBe('openai');
  });

  it('Save serialises Enabled + the profile list, sending null for an unset API key', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Llm', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('checkbox', { name: /^(Aktiviert|Enabled)$/i }));
    clickLlmSave();

    await waitFor(() => {

      const body = putBody as any;
      expect(body?.Enabled).toBe(true);
      expect(body?.ActiveProfileId).toBe('openai');
      expect(body?.Profiles).toHaveLength(1);
      expect(body.Profiles[0].Id).toBe('openai');
      expect(body.Profiles[0].Model).toBe('gpt');
      // apiKey was null in the snapshot → keep-mode doesn't apply → null sent unchanged.
      expect(body.Profiles[0].ApiKey).toBeNull();
      expect(body.Profiles[0].EnableToolCalling).toBe(false);
      expect(body.Profiles[0].ToolCallMaxDepth).toBe(4);
    });
  });

  it('successful save refreshes the AI-capabilities query (gates every AI entry point)', async () => {
    server.use(http.put('/api/admin/settings/Llm', () =>
      HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' })));

    const { qc } = renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());
    const invalidate = vi.spyOn(qc, 'invalidateQueries');

    clickLlmSave();

    // The delayed second invalidation (IOptionsMonitor grace) is pinned in useAiCapabilities.test.
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['ai-knowledge-capabilities'] }));
  });

  it('sends __unchanged__ for a stored API key the operator did not retype', async () => {
    let putBody: unknown = null;
    server.use(
      http.get('/api/admin/settings/Llm', () => HttpResponse.json({
        ...llmSnapshot,
        payload: { ...llmSnapshot.payload, profiles: [llmProfile({ apiKey: '********' })] },
      })),
      http.put('/api/admin/settings/Llm', async ({ request }) => {
        putBody = await request.json();
        return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
      }),
    );

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());
    clickLlmSave();

    await waitFor(() => {

      const body = putBody as any;
      expect(body.Profiles[0].ApiKey).toBe('__unchanged__');
    });
  });

  it('toggles tool-calling per profile, reveals the depth input, and round-trips both', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Llm', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());

    const toolCalling = screen.getByRole('checkbox', { name: /Tool-Calling|Tool-calling/i }) as HTMLInputElement;
    expect(toolCalling.checked).toBe(false);
    fireEvent.click(toolCalling);

    const depth = await screen.findByDisplayValue('4');
    fireEvent.change(depth, { target: { value: '6' } });

    clickLlmSave();

    await waitFor(() => {

      const body = putBody as any;
      expect(body.Profiles[0].EnableToolCalling).toBe(true);
      expect(body.Profiles[0].ToolCallMaxDepth).toBe(6);
    });
  });

  it('adds a second profile, slugs its id, and keeps the first one intact', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Llm', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('http://127.0.0.1:1234/v1')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Profil hinzufügen|Add profile/i }));

    // The new profile becomes the selected one; rename it.
    const nameInput = await screen.findByDisplayValue(/Neues Profil|New profile/i);
    fireEvent.change(nameInput, { target: { value: 'Local Ollama' } });

    clickLlmSave();

    await waitFor(() => {

      const body = putBody as any;
      expect(body.Profiles).toHaveLength(2);
      expect(body.Profiles.map((p: { Id: string }) => p.Id)).toContain('openai');
      // The id is slugged from the name at creation and stays put through the later rename.
      const added = body.Profiles.find((p: { Id: string }) => p.Id !== 'openai');
      expect(added.Id).toMatch(/^[a-z0-9][a-z0-9-]*$/);
      expect(added.Name).toBe('Local Ollama');
    });
  });

  it('deletes a runtime-owned profile and falls the active selection back to the survivor', async () => {
    let putBody: unknown = null;
    server.use(
      http.get('/api/admin/settings/Llm', () => HttpResponse.json({
        ...llmSnapshot,
        payload: {
          enabled: true,
          activeProfileId: 'openai',
          profiles: [llmProfile(), llmProfile({ id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1' })],
        },
      })),
      http.put('/api/admin/settings/Llm', async ({ request }) => {
        putBody = await request.json();
        return HttpResponse.json({ ...llmSnapshot, etag: '"llm-2"' });
      }),
    );

    renderSection();
    await screen.findByRole('button', { name: /Profil löschen|Delete profile/i });

    fireEvent.click(screen.getByRole('button', { name: /Profil löschen|Delete profile/i }));
    clickLlmSave();

    await waitFor(() => {

      const body = putBody as any;
      expect(body.Profiles).toHaveLength(1);
      expect(body.Profiles[0].Id).toBe('ollama');
      expect(body.ActiveProfileId).toBe('ollama');
    });
  });

  it('disables Delete for a profile owned by another configuration source', async () => {
    server.use(http.get('/api/admin/settings/Llm', () => HttpResponse.json({
      ...llmSnapshot,
      payload: { ...llmSnapshot.payload, profiles: [llmProfile({ managedBy: 'appsettings' })] },
    })));

    renderSection();
    await screen.findByRole('button', { name: /Profil löschen|Delete profile/i });

    expect(screen.getByRole('button', { name: /Profil löschen|Delete profile/i })).toBeDisabled();
    expect(screen.getByText(/appsettings/)).toBeInTheDocument();
  });

  it('surfaces an inline error when enabled without an active profile', async () => {
    server.use(http.get('/api/admin/settings/Llm', () => HttpResponse.json({
      ...llmSnapshot,
      payload: { enabled: true, activeProfileId: '', profiles: [] },
    })));

    renderSection();
    await waitFor(() => expect(screen.getByText(/Noch kein LLM-Profil|No LLM profile yet/i)).toBeInTheDocument());

    expect(screen.getByText(/Wähle ein aktives Profil|Select an active profile/i)).toBeInTheDocument();
  });
});
