import { describe, it, expect, vi, beforeAll, afterAll, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { GlobalVariablesPage } from '../../pages/GlobalVariablesPage';
import { useAuthStore } from '../../stores/authStore';
import { ROOT_FOLDER_ID } from '../../api/globalFolders';

const BASE = 'http://localhost';
const originalMatchMedia = window.matchMedia;

const FOLDERS = [
  { id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0, createdAt: '2026-06-01T00:00:00Z', createdByUserId: null, variableCount: 2 },
];

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

function setMobile() {
  window.matchMedia = ((q: string) => ({
    matches: true, media: q, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => setMobile());
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); window.matchMedia = originalMatchMedia; });
afterAll(() => server.close());

const VARS = [
  { id: 'g1', name: 'API_BASE', value: 'https://api', isSecret: false, description: 'base url', folderId: ROOT_FOLDER_ID, createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
  { id: 'g2', name: 'TOKEN', value: null, isSecret: true, description: null, folderId: ROOT_FOLDER_ID, createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
];

function renderPage() {
  useAuthStore.setState({ isAuthenticated: true, username: 'admin', role: 'Admin', userId: 'u1' });
  patchFetch();
  server.use(http.get(`${BASE}/api/global-variable-folders`, () => HttpResponse.json(FOLDERS)));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <GlobalVariablesPage />
    </QueryClientProvider>,
  );
}

describe('GlobalVariablesPage — mobile', () => {
  it('renders cards instead of a table and masks secret values', async () => {
    server.use(http.get(`${BASE}/api/global-variables`, () => HttpResponse.json(VARS)));
    renderPage();

    await waitFor(() => expect(screen.getByText('API_BASE')).toBeInTheDocument());
    expect(screen.getByTestId('mobile-card-list')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getByText('TOKEN')).toBeInTheDocument();
    // Secret value stays masked in the card.
    expect(screen.getByText('***')).toBeInTheDocument();
  });
});
