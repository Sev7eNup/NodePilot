import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { SecuritySection } from '../../../components/admin-settings/SecuritySection';
import { useAuthStore } from '../../../stores/authStore';
import { useToastStore } from '../../../stores/toastStore';
import { confirmDialog } from '../../../stores/confirmStore';

vi.mock('../../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => {
  vi.mocked(confirmDialog).mockClear().mockResolvedValue(true);
  useToastStore.setState({ toasts: [] });
  useAuthStore.setState({ role: null }); // default: no role → re-encrypt card hidden
});
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const restApi = {
  sectionPath: 'RestApi',
  payload: {
    blockPrivateNetworks: true,
    allowedHosts: ['api.internal.example'],
    proxy: { enabled: false, address: '', bypassList: [], username: null, password: null },
  },
  etag: '"r-1"', isHotReloadable: false, effectiveSource: {},
};
const fso = { sectionPath: 'FileSystemOperation', payload: { rejectTraversal: true, allowedRoots: [] }, etag: '"f-1"', isHotReloadable: true, effectiveSource: {} };
const sql = { sectionPath: 'SqlActivity', payload: { requireConnectionRef: false }, etag: '"s-1"', isHotReloadable: true, effectiveSource: {} };
const sp = { sectionPath: 'StartProgram', payload: { disallowShellExecute: true }, etag: '"sp-1"', isHotReloadable: true, effectiveSource: {} };
const wh = { sectionPath: 'Webhook', payload: { requireSecret: true }, etag: '"wh-1"', isHotReloadable: true, effectiveSource: {} };
const et = { sectionPath: 'ExternalTrigger', payload: { apiKey: '********' }, etag: '"et-1"', isHotReloadable: true, effectiveSource: {} };
const sec = { sectionPath: 'Security', payload: { strictAllowedHosts: false, allowedHosts: '*' }, etag: '"sec-1"', isHotReloadable: false, effectiveSource: {} };

function renderAll() {
  server.use(
    http.get('/api/admin/settings/RestApi', () => HttpResponse.json(restApi)),
    http.get('/api/admin/settings/FileSystemOperation', () => HttpResponse.json(fso)),
    http.get('/api/admin/settings/SqlActivity', () => HttpResponse.json(sql)),
    http.get('/api/admin/settings/StartProgram', () => HttpResponse.json(sp)),
    http.get('/api/admin/settings/Webhook', () => HttpResponse.json(wh)),
    http.get('/api/admin/settings/ExternalTrigger', () => HttpResponse.json(et)),
    http.get('/api/admin/settings/Security', () => HttpResponse.json(sec)),
  );
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><SecuritySection /></QueryClientProvider>);
}

describe('SecuritySection', () => {
  it('renders all seven cards with persisted toggle state', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });
    // ExternalTrigger card shows the masked key when set.
    expect(screen.getByDisplayValue('********')).toBeInTheDocument();
    // Multiple "Speichern" buttons confirm each card has its own save flow.
    expect(screen.getAllByRole('button', { name: /speichern|save/i }).length).toBeGreaterThanOrEqual(7);
  });

  it('shows the hot-reload hint on the five live hardening cards but not on RestApi/Security', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });
    // FileSystemOperation / SqlActivity / StartProgram / Webhook / ExternalTrigger → 5 hints.
    expect(screen.getAllByText(/Changes apply immediately/i).length).toBe(5);
  });

  it('Security card Save sends StrictAllowedHosts + AllowedHosts in PascalCase', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Security', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...sec, etag: '"sec-2"' });
    }));
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });
    // Last save button = Security card (rendered last in the section list).
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    fireEvent.click(saves[saves.length - 1]);
    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.AllowedHosts).toBe('*');
      expect(body?.StrictAllowedHosts).toBe(false);
    });
  });

  it('reencrypt card is hidden without the Admin role', async () => {
    useAuthStore.setState({ role: 'Operator' });
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });
    expect(screen.queryByRole('button', { name: /re-encrypt now/i })).not.toBeInTheDocument();
  });

  it('Admin reencrypt: renders, confirms, POSTs and toasts the counts', async () => {
    useAuthStore.setState({ role: 'Admin' });
    let posted = false;
    server.use(http.post('/api/secrets/reencrypt', () => {
      posted = true;
      return HttpResponse.json({
        credentialsRewritten: 3, credentialsSkipped: 0, credentialSkipDetails: [],
        globalSecretsRewritten: 2, globalSecretsSkipped: 0, globalSecretSkipDetails: [],
        partialSuccess: false,
      });
    }));
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });

    fireEvent.click(screen.getByRole('button', { name: /re-encrypt now/i }));
    expect(confirmDialog).toHaveBeenCalledWith(expect.objectContaining({ danger: true }));

    await waitFor(() => expect(posted).toBe(true));
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((x) =>
        x.kind === 'success' && x.message.includes('3') && x.message.includes('2')),
    ).toBe(true));
  });

  it('Admin reencrypt: cancelled confirm does not POST', async () => {
    useAuthStore.setState({ role: 'Admin' });
    let posted = false;
    server.use(http.post('/api/secrets/reencrypt', () => {
      posted = true;
      return HttpResponse.json({});
    }));
    vi.mocked(confirmDialog).mockResolvedValue(false);
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });

    fireEvent.click(screen.getByRole('button', { name: /re-encrypt now/i }));
    await new Promise((r) => setTimeout(r, 50));
    expect(posted).toBe(false);
  });

  it('Admin reencrypt: 207 partial success surfaces an error toast', async () => {
    useAuthStore.setState({ role: 'Admin' });
    server.use(http.post('/api/secrets/reencrypt', () =>
      HttpResponse.json({
        credentialsRewritten: 4, credentialsSkipped: 1,
        credentialSkipDetails: [{ name: 'old-cred', reason: 'DecryptFailed' }],
        globalSecretsRewritten: 0, globalSecretsSkipped: 0, globalSecretSkipDetails: [],
        partialSuccess: true,
      }, { status: 207 })));
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('*')).toBeInTheDocument(), { timeout: 3000 });

    fireEvent.click(screen.getByRole('button', { name: /re-encrypt now/i }));
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((x) => x.kind === 'error' && /partial/i.test(x.message)),
    ).toBe(true));
  });
});
