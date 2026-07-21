import { describe, it, expect, vi, beforeEach, afterEach, beforeAll, afterAll } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import {
  WorkflowQuickSwitcher,
  readRecentWorkflows,
  pushRecentWorkflow,
} from '../../../components/designer/overlays/WorkflowQuickSwitcher';
import type { Workflow } from '../../../types/api';

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
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); localStorage.clear(); });
afterAll(() => server.close());

const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

beforeEach(() => navigateMock.mockReset());

function mkWorkflow(id: string, name: string, description: string | null = null): Workflow {
  return {
    id, name, description,
    definitionJson: '{}', version: 1, isEnabled: true,
    createdAt: '2026-01-01', updatedAt: '2026-01-01',
    createdBy: null, updatedBy: null,
  };
}

const MOCK: Workflow[] = [
  mkWorkflow('11111111-aaaa-bbbb-cccc-000000000001', 'Alpha workflow', 'first'),
  mkWorkflow('22222222-aaaa-bbbb-cccc-000000000002', 'Beta workflow', 'second'),
  mkWorkflow('33333333-aaaa-bbbb-cccc-000000000003', 'Gamma workflow'),
];

function renderSwitcher() {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <WorkflowQuickSwitcher onClose={vi.fn()} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('readRecentWorkflows / pushRecentWorkflow', () => {
  beforeEach(() => localStorage.clear());

  it('emptyByDefault', () => {
    expect(readRecentWorkflows()).toEqual([]);
  });

  it('pushAddsToHead', () => {
    pushRecentWorkflow('a');
    pushRecentWorkflow('b');
    expect(readRecentWorkflows()).toEqual(['b', 'a']);
  });

  it('pushDedupes_existingIdMovesToHead', () => {
    pushRecentWorkflow('a');
    pushRecentWorkflow('b');
    pushRecentWorkflow('a');
    expect(readRecentWorkflows()).toEqual(['a', 'b']);
  });

  it('capsAt10Entries', () => {
    for (let i = 0; i < 15; i++) pushRecentWorkflow(`id-${i}`);
    expect(readRecentWorkflows()).toHaveLength(10);
    // newest still at head, oldest five evicted
    expect(readRecentWorkflows()[0]).toBe('id-14');
  });

  it('corruptedStorage_returnsEmpty', () => {
    localStorage.setItem('nodepilot.recentWorkflows', 'not json');
    expect(readRecentWorkflows()).toEqual([]);
  });

  it('nonStringEntries_filteredOut', () => {
    localStorage.setItem('nodepilot.recentWorkflows', JSON.stringify(['a', 1, null, 'b']));
    expect(readRecentWorkflows()).toEqual(['a', 'b']);
  });
});

describe('WorkflowQuickSwitcher', () => {
  it('rendersAllWorkflows_whenNoQuery', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());
    expect(screen.getByText('Beta workflow')).toBeInTheDocument();
    expect(screen.getByText('Gamma workflow')).toBeInTheDocument();
  });

  it('emptyWorkflows_showsNoWorkflowsMessage', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText(/No workflows available/)).toBeInTheDocument());
  });

  it('queryNarrowsResults_byNameSubstring', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText(/Switch to workflow/i), { target: { value: 'Beta' } });

    expect(screen.getByText('Beta workflow')).toBeInTheDocument();
    expect(screen.queryByText('Alpha workflow')).not.toBeInTheDocument();
  });

  it('queryWithoutMatches_showsNoMatchHint', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText(/Switch to workflow/i), { target: { value: 'zzz' } });

    expect(screen.getByText(/No workflows matching "zzz"/)).toBeInTheDocument();
  });

  it('clickingARow_navigatesAndPushesRecent', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Alpha workflow'));

    // No currentId in URL (non-editor route) → navigate with undefined state
    expect(navigateMock).toHaveBeenCalledWith(`/workflows/${MOCK[0].id}`, { state: undefined });
    expect(readRecentWorkflows()).toEqual([MOCK[0].id]);
  });

  it('enterKey_navigatesToHighlightedWorkflow', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());

    const input = screen.getByPlaceholderText(/Switch to workflow/i);
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    // No currentId in URL (non-editor route) → navigate with undefined state
    expect(navigateMock).toHaveBeenCalledWith(`/workflows/${MOCK[1].id}`, { state: undefined });
  });

  it('escapeKey_closes', async () => {
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    const onClose = vi.fn();
    patchFetch();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <WorkflowQuickSwitcher onClose={onClose} />
        </MemoryRouter>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByText('Alpha workflow')).toBeInTheDocument());
    fireEvent.keyDown(screen.getByPlaceholderText(/Switch to workflow/i), { key: 'Escape' });

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('recentWorkflowsRenderedFirst', async () => {
    pushRecentWorkflow(MOCK[2].id); // gamma is recent
    server.use(http.get(`${BASE}/api/workflows`, () => HttpResponse.json(MOCK)));
    renderSwitcher();

    await waitFor(() => expect(screen.getByText('Gamma workflow')).toBeInTheDocument());

    const allRows = screen.getAllByRole('button').filter((b) =>
      b.textContent?.includes('workflow')
    );
    // Gamma should appear before Alpha/Beta in the list since it's the recent.
    expect(allRows[0].textContent).toContain('Gamma workflow');
  });
});
