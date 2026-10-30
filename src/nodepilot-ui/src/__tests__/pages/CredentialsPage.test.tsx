import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { CredentialsPage } from '../../pages/CredentialsPage';
import { useAuthStore } from '../../stores/authStore';
import { useToastStore } from '../../stores/toastStore';
import type { Credential } from '../../types/api';

const BASE = 'http://localhost';
const DAY = 86_400_000;

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const CREDS: Credential[] = [
  { id: 'c1', name: 'svc-windows', username: 'svc-acct', domain: 'CORP', expiresAt: null },
  { id: 'c2', name: 'local-admin', username: 'admin', domain: null, expiresAt: null },
];

// The route admits Admin and Operator only, so those are the two roles the page renders for.
function renderPage(role: 'Admin' | 'Operator' = 'Admin') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/credentials']}>
        <CredentialsPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('CredentialsPage', () => {
  it('rendersSubtitle_andAddButton', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    expect(screen.getByText(/Stored accounts NodePilot uses/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument();
  });

  it('emptyCredentials_showsEmptyMessage', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();
    await waitFor(() => expect(screen.getByText(/No credentials stored/)).toBeInTheDocument());
  });

  it('rendersCredentialList_withDomainAndUsername', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
    expect(screen.getByText('CORP\\svc-acct')).toBeInTheDocument();
    expect(screen.getByText('admin')).toBeInTheDocument();
  });

  it('eachRow_linksToItsDependencies', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
    const links = screen.getAllByRole('link', { name: 'Show dependencies' });
    expect(links).toHaveLength(CREDS.length);
    expect(links[0].getAttribute('href')).toContain('kind=credential');
    expect(links[0].getAttribute('href')).toContain('id=c1');
  });

  it('clickAdd_opensCreateForm', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));

    const form = screen.getByRole('group', { name: /Add Credential/i });
    expect(form).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Credential Name')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Username')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Password')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Domain (optional)')).toBeInTheDocument();
  });

  it('cancelCreate_hidesForm', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByPlaceholderText('Credential Name')).not.toBeInTheDocument();
  });

  it('saveCreate_postsTheFormValues', async () => {
    let postBody: Record<string, unknown> | null = null;
    server.use(
      http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])),
      http.post(`${BASE}/api/credentials`, async ({ request }) => {
        postBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ id: 'new', name: 'svc-new', username: 'svc', domain: null, expiresAt: null }, { status: 201 });
      }),
    );
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));
    fireEvent.change(screen.getByPlaceholderText('Credential Name'), { target: { value: 'svc-new' } });
    fireEvent.change(screen.getByPlaceholderText('Username'), { target: { value: 'svc' } });
    fireEvent.change(screen.getByPlaceholderText('Password'), { target: { value: 'Secret123!' } });
    fireEvent.change(screen.getByLabelText(/Expiry date/i), { target: { value: '2099-01-31' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(postBody).not.toBeNull());
    expect(postBody).toEqual({
      name: 'svc-new',
      username: 'svc',
      password: 'Secret123!',
      domain: null,
      expiresAt: '2099-01-31T00:00:00Z',
    });
    await waitFor(() =>
      expect(screen.queryByPlaceholderText('Credential Name')).not.toBeInTheDocument());
  });

  it('operatorRole_canEditButNotDelete', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Operator');

    await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /Edit credential/i })).toHaveLength(CREDS.length);
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });

  it('adminRole_seesDeleteButtonsForEachCredential', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
    const deleteButtons = screen.getAllByRole('button', { name: /delete/i });
    expect(deleteButtons).toHaveLength(CREDS.length);
  });

  it('formInputs_acceptKeystrokes', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));

    const nameInput = screen.getByPlaceholderText('Credential Name') as HTMLInputElement;
    fireEvent.change(nameInput, { target: { value: 'My Cred' } });
    expect(nameInput.value).toBe('My Cred');

    const passwordInput = screen.getByPlaceholderText('Password') as HTMLInputElement;
    expect(passwordInput.type).toBe('password');
  });

  describe('edit flow + expiry', () => {
    beforeEach(() => {
      useToastStore.setState({ toasts: [] });
    });

    const CRED_WITH_EXPIRY: Credential = {
      id: 'c1', name: 'svc-windows', username: 'svc-acct', domain: 'CORP',
      expiresAt: '2099-08-01T00:00:00Z',
    };

    it('editButton_opensPrefilledPanel_withEmptyPassword', async () => {
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([CRED_WITH_EXPIRY])));
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: /Edit credential/i }));

      expect(screen.getByRole('group', { name: /Edit credential/i })).toBeInTheDocument();
      expect((screen.getByPlaceholderText('Credential Name') as HTMLInputElement).value).toBe('svc-windows');
      expect((screen.getByPlaceholderText('Domain (optional)') as HTMLInputElement).value).toBe('CORP');
      expect((screen.getByPlaceholderText('Username') as HTMLInputElement).value).toBe('svc-acct');
      expect((screen.getByPlaceholderText('Password') as HTMLInputElement).value).toBe('');
      expect((screen.getByLabelText(/Expiry date/i) as HTMLInputElement).value).toBe('2099-08-01');
      expect(screen.getByText(/Leave blank to keep the current password/i)).toBeInTheDocument();
    });

    it('saveEdit_putsPasswordNullWhenBlank_andIncludesExpiresAt', async () => {
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get(`${BASE}/api/credentials`, () => HttpResponse.json([CRED_WITH_EXPIRY])),
        http.put(`${BASE}/api/credentials/c1`, async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({});
        }),
      );
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: /Edit credential/i }));
      fireEvent.change(screen.getByLabelText(/Expiry date/i), { target: { value: '2099-09-15' } });
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(putBody).not.toBeNull());
      expect(putBody).toEqual({
        name: 'svc-windows',
        username: 'svc-acct',
        password: null,
        domain: 'CORP',
        expiresAt: '2099-09-15T00:00:00Z',
      });
      // Success closes the panel and pushes a toast.
      await waitFor(() =>
        expect(screen.queryByPlaceholderText('Credential Name')).not.toBeInTheDocument());
      expect(useToastStore.getState().toasts.some((t) => t.kind === 'success')).toBe(true);
    });

    it('saveEdit_sendsTypedPassword_whenNotBlank', async () => {
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get(`${BASE}/api/credentials`, () => HttpResponse.json([CRED_WITH_EXPIRY])),
        http.put(`${BASE}/api/credentials/c1`, async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({});
        }),
      );
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: /Edit credential/i }));
      fireEvent.change(screen.getByPlaceholderText('Password'), { target: { value: 'n3w-secret' } });
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(putBody).not.toBeNull());
      expect(putBody).toMatchObject({ password: 'n3w-secret' });
    });

    it('saveEdit_renameOnly_preservesOriginalExpiresAtTimeOfDay', async () => {
      // CLI-set timestamp with a real time-of-day — a rename must not rewrite it to midnight.
      const cliSet: Credential = { ...CRED_WITH_EXPIRY, expiresAt: '2099-08-01T18:00:00Z' };
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get(`${BASE}/api/credentials`, () => HttpResponse.json([cliSet])),
        http.put(`${BASE}/api/credentials/c1`, async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({});
        }),
      );
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: /Edit credential/i }));
      fireEvent.change(screen.getByPlaceholderText('Credential Name'), { target: { value: 'svc-windows-renamed' } });
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(putBody).not.toBeNull());
      expect(putBody).toMatchObject({ name: 'svc-windows-renamed', expiresAt: '2099-08-01T18:00:00Z' });
    });

    it('expiredCredential_rendersRedExpiredBadge', async () => {
      const expired: Credential = { ...CRED_WITH_EXPIRY, expiresAt: '2020-01-01T00:00:00Z' };
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([expired])));
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      const badge = screen.getByText('Expired');
      expect(badge).toBeInTheDocument();
      expect(badge.className).toContain('text-red-600');
    });

    it('expiringSoonCredential_rendersAmberCountdownBadge_withWholeDaysLeft', async () => {
      const soon: Credential = { ...CRED_WITH_EXPIRY, expiresAt: new Date(Date.now() + 5.5 * DAY).toISOString() };
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([soon])));
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      const badge = screen.getByText(/expires in 5d/i);
      expect(badge).toBeInTheDocument();
      expect(badge.className).toContain('text-amber-600');
    });

    it('laterExpiry_rendersPlainDate', async () => {
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([CRED_WITH_EXPIRY])));
      renderPage();

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      expect(screen.queryByText('Expired')).not.toBeInTheDocument();
      expect(screen.queryByText(/expires in/i)).not.toBeInTheDocument();
      expect(screen.getByText(/2099/)).toBeInTheDocument();
    });
  });
});

describe('CredentialsPage editing sessions', () => {
  it('locks the draft while saving and keeps a credential opened later when the old save completes', async () => {
    let release = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    let listCalls = 0;
    server.use(
      http.get(`${BASE}/api/credentials`, () => { listCalls++; return HttpResponse.json(CREDS); }),
      http.put(`${BASE}/api/credentials/c1`, async () => { await gate; return new HttpResponse(null, { status: 204 }); }),
    );
    renderPage();

    const editButtons = await screen.findAllByRole('button', { name: 'Edit credential' });
    fireEvent.click(editButtons[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(screen.getByDisplayValue('svc-windows')).toBeDisabled());
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();

    fireEvent.click(editButtons[1]);
    expect(screen.getByDisplayValue('local-admin')).toBeInTheDocument();
    const callsBeforeRelease = listCalls;
    release();

    await waitFor(() => expect(listCalls).toBeGreaterThan(callsBeforeRelease));
    expect(screen.getByRole('group', { name: 'Edit credential' })).toBeInTheDocument();
    expect(screen.getByDisplayValue('local-admin')).toBeEnabled();
  });
});
