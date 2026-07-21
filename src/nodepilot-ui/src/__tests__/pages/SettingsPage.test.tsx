import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { SettingsPage } from '../../pages/SettingsPage';
import { useAuthStore } from '../../stores/authStore';
import { useToastStore } from '../../stores/toastStore';
import type { Credential } from '../../types/api';

const BASE = 'http://localhost';

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

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

function renderPage(role: 'Admin' | 'Operator' | 'Viewer' = 'Admin', path = '/settings') {
  useAuthStore.setState({ isAuthenticated: true, username: 'u', role });
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <SettingsPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('SettingsPage', () => {
  it('writes top-level tab changes to the URL', () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    const { container } = renderPage('Admin');
    const systemTab = [...container.querySelectorAll<HTMLButtonElement>('.np-tab-list button')]
      .find((button) => button.textContent?.trim() === 'System');
    fireEvent.click(systemTab!);
    expect(screen.getByTestId('location')).toHaveTextContent('/settings?tab=system&section=integrations');
  });

  it('falls back to personal for a non-admin system deep link', () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    const { container } = renderPage('Viewer', '/settings?tab=system&section=security');
    expect(screen.getByRole('heading', { name: /credentials/i })).toBeInTheDocument();
    expect(container.querySelector('.np-tab-list')).not.toBeInTheDocument();
  });

  it('rendersCredentialsSection', () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage();
    // The page title now lives in the app TopBar; assert the Credentials section heading.
    expect(screen.getByRole('heading', { name: /credentials/i })).toBeInTheDocument();
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

  it('adminUser_seesAddCredentialButton', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByText(/No credentials stored/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument();
  });

  it('viewerRole_hidesAddButton', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText(/No credentials stored/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Add Credential/i })).not.toBeInTheDocument();
  });

  it('clickAdd_opensCreateForm', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));

    expect(screen.getByPlaceholderText('Credential Name')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Username')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Password')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Domain (optional)')).toBeInTheDocument();
  });

  it('cancelCreate_hidesForm', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Add Credential/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByPlaceholderText('Credential Name')).not.toBeInTheDocument();
  });

  it('viewerRole_hidesDeleteButton', async () => {
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json(CREDS)));
    renderPage('Viewer');

    await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
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
    renderPage('Admin');

    await waitFor(() => expect(screen.getByRole('button', { name: /Add Credential/i })).toBeInTheDocument());
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
      renderPage('Admin');

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: /Edit credential/i }));

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
      renderPage('Admin');

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
      renderPage('Admin');

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
      renderPage('Admin');

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
      renderPage('Admin');

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      const badge = screen.getByText('Expired');
      expect(badge).toBeInTheDocument();
      expect(badge.className).toContain('text-red-600');
    });

    it('expiringSoonCredential_rendersAmberCountdownBadge', async () => {
      const soon: Credential = {
        ...CRED_WITH_EXPIRY,
        expiresAt: new Date(Date.now() + 5 * 86_400_000).toISOString(),
      };
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([soon])));
      renderPage('Admin');

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      const badge = screen.getByText(/expires in 5d/i);
      expect(badge).toBeInTheDocument();
      expect(badge.className).toContain('text-amber-600');
    });

    it('viewerRole_hidesEditButton', async () => {
      server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([CRED_WITH_EXPIRY])));
      renderPage('Viewer');

      await waitFor(() => expect(screen.getByText('svc-windows')).toBeInTheDocument());
      expect(screen.queryByRole('button', { name: /Edit credential/i })).not.toBeInTheDocument();
    });
  });
});
