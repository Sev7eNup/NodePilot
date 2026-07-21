import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { UsersPage } from '../../pages/UsersPage';
import { useAuthStore } from '../../stores/authStore';
import type { UserRow } from '../../types/api';

vi.mock('../../stores/confirmStore', () => ({
  confirmDialog: vi.fn().mockResolvedValue(true),
}));

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

function renderPage() {
  useAuthStore.setState({ isAuthenticated: true, username: 'admin', role: 'Admin' });
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <UsersPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const MOCK_USERS: UserRow[] = [
  { id: 'u1', username: 'admin', role: 'Admin', isActive: true, createdAt: new Date().toISOString() },
  {
    id: 'u2', username: 'operator1', role: 'Operator', isActive: true, createdAt: new Date().toISOString(),
    provider: 'ActiveDirectory', authority: 'corp.example', subject: 'S-1-5-21-1-2-3-1105',
    lastDirectorySyncAt: '2026-07-12T08:00:00Z', directorySyncStatus: 'Current',
  },
  { id: 'u3', username: 'viewer1', role: 'Viewer', isActive: false, createdAt: new Date().toISOString() },
];

describe('UsersPage', () => {
  it('shows loading state initially', () => {
    server.use(http.get(`${BASE}/api/users`, () => new Promise(() => {})));
    renderPage();
    expect(screen.getByText(/loading/i)).toBeInTheDocument();
  });

  it('renders user list with usernames', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('admin')).toBeInTheDocument());
    expect(screen.getByText('operator1')).toBeInTheDocument();
    expect(screen.getByText('viewer1')).toBeInTheDocument();
  });

  it('shows role badges for all three roles', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Admin')).toBeInTheDocument());
    expect(screen.getByText('Operator')).toBeInTheDocument();
    expect(screen.getByText('Viewer')).toBeInTheDocument();
  });

  it('shows inactive indicator for disabled user', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('viewer1')).toBeInTheDocument());
    // viewer1 is inactive — the page should show some indicator
    expect(screen.getByText(/inactive/i)).toBeInTheDocument();
  });

  it('shows provider identity and directory synchronization metadata', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('operator1')).toBeInTheDocument());
    expect(screen.getByText('ActiveDirectory')).toBeInTheDocument();
    expect(screen.getByText(/corp\.example/)).toBeInTheDocument();
    expect(screen.getByText(/S-1-5-21-1-2-3-1105/)).toBeInTheDocument();
    expect(screen.getByText('Current')).toBeInTheDocument();
  });

  it('renders an external-identity tombstone explicitly', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json([
      { ...MOCK_USERS[1], isActive: false, isTombstoned: true },
    ])));
    renderPage();
    await waitFor(() => expect(screen.getByText('operator1')).toBeInTheDocument());
    expect(screen.getByText('Tombstoned')).toBeInTheDocument();
    expect(screen.queryByTitle(/reset password/i)).not.toBeInTheDocument();
  });

  it('reactivates a tombstoned identity through the dedicated endpoint', async () => {
    let reactivated = false;
    server.use(
      http.get(`${BASE}/api/users`, () => HttpResponse.json([
        { ...MOCK_USERS[1], isActive: false, isTombstoned: true },
      ])),
      http.post(`${BASE}/api/users/u2/reactivate`, () => {
        reactivated = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderPage();
    const button = await screen.findByRole('button', { name: /reactivate external identity/i });
    fireEvent.click(button);
    await waitFor(() => expect(reactivated).toBe(true));
  });

  it('shows New User button', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('admin')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /new user/i })).toBeInTheDocument();
  });

  it('opens create dialog when New User is clicked', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /new user/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /new user/i }));
    // Dialog is open when a second "Username" appears — table header already exists,
    // the dialog adds a <label>Username</label>, making the count >= 2.
    await waitFor(() => expect(screen.getAllByText('Username').length).toBeGreaterThanOrEqual(2));
  });

  it('creates an explicitly designated break-glass account', async () => {
    let postBody: unknown = null;
    server.use(
      http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)),
      http.post(`${BASE}/api/users`, async ({ request }) => {
        postBody = await request.json();
        return HttpResponse.json(MOCK_USERS[0], { status: 201 });
      }),
    );
    renderPage();
    await waitFor(() => expect(screen.getByRole('button', { name: /new user/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /new user/i }));

    const textboxes = screen.getAllByRole('textbox');
    fireEvent.change(textboxes.at(-1)!, { target: { value: 'emergency-admin' } });
    const password = document.querySelector('input[type="password"]') as HTMLInputElement;
    fireEvent.change(password, { target: { value: 'safe-password-123' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /designated break-glass account/i }));
    fireEvent.click(screen.getByRole('button', { name: /^create$/i }));

    await waitFor(() => expect(postBody).not.toBeNull());
    expect(postBody).toMatchObject({ username: 'emergency-admin', isBreakGlass: true });
  });

  it('shows edit button per user row', async () => {
    server.use(http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('admin')).toBeInTheDocument());
    // Each row has an edit button
    const editButtons = screen.getAllByRole('button', { name: /edit/i });
    expect(editButtons.length).toBeGreaterThanOrEqual(1);
  });

  it('updates break-glass designation only for a local account', async () => {
    let putBody: unknown = null;
    server.use(
      http.get(`${BASE}/api/users`, () => HttpResponse.json(MOCK_USERS)),
      http.put(`${BASE}/api/users/u1`, async ({ request }) => {
        putBody = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderPage();
    const editButtons = await screen.findAllByRole('button', { name: /edit role/i });
    fireEvent.click(editButtons[0]);
    fireEvent.click(screen.getByRole('checkbox', { name: /designated break-glass account/i }));
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => expect(putBody).not.toBeNull());
    expect(putBody).toMatchObject({ isBreakGlass: true });
  });
});
