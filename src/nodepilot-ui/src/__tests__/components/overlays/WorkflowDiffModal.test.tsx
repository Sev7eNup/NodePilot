import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { Node, Edge } from '@xyflow/react';
import { WorkflowDiffModal } from '../../../components/designer/overlays/WorkflowDiffModal';

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
vi.mock('../../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});

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

function node(id: string, label: string, extra: Partial<Node> = {}): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label }, ...extra };
}
function edge(id: string, source: string, target: string, label = 'On Success'): Edge {
  return { id, source, target, type: 'labeled', data: { label } };
}

function renderModal(currentNodes: Node[], currentEdges: Edge[], opts: { canRestore?: boolean; onClose?: () => void } = {}) {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <WorkflowDiffModal
        workflowId="wf-1"
        currentDefinition={{ nodes: currentNodes, edges: currentEdges }}
        canRestore={opts.canRestore}
        onClose={opts.onClose ?? vi.fn()}
      />
    </QueryClientProvider>
  );
}

describe('WorkflowDiffModal', () => {
  it('rendersHeader', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () => HttpResponse.json([])),
    );
    renderModal([], []);
    await waitFor(() => expect(screen.getByText(/Workflow Diff/)).toBeInTheDocument());
  });

  it('emptyVersionList_showsNoHistoryMessage', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([{ version: 1, isCurrent: true }])
      ),
    );
    renderModal([], []);
    await waitFor(() => expect(screen.getByText(/No history/)).toBeInTheDocument());
  });

  it('listsHistoricalVersionsExceptCurrent', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 3, isCurrent: true },
          { version: 2, isCurrent: false, createdAt: '2026-04-25T10:00:00Z', createdBy: 'admin' },
          { version: 1, isCurrent: false, createdAt: '2026-04-20T10:00:00Z' },
        ])
      ),
    );
    renderModal([], []);

    await waitFor(() => expect(screen.getByText('Version 2')).toBeInTheDocument());
    expect(screen.getByText('Version 1')).toBeInTheDocument();
    // Version 3 is the current — must be hidden.
    expect(screen.queryByText('Version 3')).not.toBeInTheDocument();
  });

  it('selectingAVersion_loadsAndDisplaysDiff', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 2, isCurrent: true },
          { version: 1, isCurrent: false, createdAt: '2026-04-25T10:00:00Z' },
        ])
      ),
      http.get(`${BASE}/api/workflows/wf-1/versions/1`, () =>
        HttpResponse.json({
          definition: {
            nodes: [{ id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'OldA' } }],
            edges: [],
          },
        })
      ),
    );

    // Current has nodeA (changed) + nodeB (added) — node from base "removed".
    renderModal(
      [
        node('a', 'NewA'), // changed: data differs
        node('b', 'NewB'), // added
      ],
      []
    );

    await waitFor(() => expect(screen.getByText('Version 1')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Version 1'));

    await waitFor(() => expect(screen.getByText('Added')).toBeInTheDocument());
    // Stats: 1 added (b), 0 removed, 1 changed (a)
    expect(screen.getByText('Nodes added')).toBeInTheDocument();
    expect(screen.getByText('Nodes changed')).toBeInTheDocument();
  });

  it('emptyDiff_rendersNoDifferencesMessage', async () => {
    const sharedNode = node('a', 'Same');
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 2, isCurrent: true },
          { version: 1, isCurrent: false, createdAt: '2026-04-25T10:00:00Z' },
        ])
      ),
      http.get(`${BASE}/api/workflows/wf-1/versions/1`, () =>
        HttpResponse.json({ definition: { nodes: [sharedNode], edges: [] } })
      ),
    );

    renderModal([sharedNode], []);
    await waitFor(() => expect(screen.getByText('Version 1')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Version 1'));

    await waitFor(() => expect(screen.getByText('No differences.')).toBeInTheDocument());
  });

  it('addedAndRemovedEdges_listedSeparately', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 2, isCurrent: true },
          { version: 1, isCurrent: false, createdAt: '2026-04-25T10:00:00Z' },
        ])
      ),
      http.get(`${BASE}/api/workflows/wf-1/versions/1`, () =>
        HttpResponse.json({
          definition: {
            nodes: [node('a', 'A'), node('b', 'B')],
            edges: [edge('e1', 'a', 'b')],
          },
        })
      ),
    );

    // Current: same nodes but a different edge
    renderModal([node('a', 'A'), node('b', 'B')], [edge('e2', 'b', 'a')]);

    await waitFor(() => expect(screen.getByText('Version 1')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Version 1'));

    await waitFor(() => expect(screen.getByText(/^\+ b → a$/)).toBeInTheDocument());
    expect(screen.getByText(/^− a → b$/)).toBeInTheDocument();
  });

  it('beforeSelectingVersion_showsPickHint', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([{ version: 1, isCurrent: false }])
      ),
    );
    renderModal([], []);
    await waitFor(() => expect(screen.getByText(/Pick a version/)).toBeInTheDocument());
  });

  it('restoreButton_requiresWriteAccess', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 2, isCurrent: true },
          { version: 1, isCurrent: false },
        ])
      ),
      http.get(`${BASE}/api/workflows/wf-1/versions/1`, () =>
        HttpResponse.json({ definition: { nodes: [], edges: [] } })
      ),
    );

    renderModal([], []);
    await waitFor(() => expect(screen.getByText('Version 1')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Version 1'));

    const restore = await screen.findByRole('button', { name: /Restore v1/ });
    expect(restore).toBeDisabled();
  });

  it('restoreButton_confirmsAndPostsRollbackWhenWritable', async () => {
    const onClose = vi.fn();
    const rollbackSeen = vi.fn();
    // confirmDialog mock resolves true by default (user confirms).
    server.use(
      http.get(`${BASE}/api/workflows/wf-1/versions`, () =>
        HttpResponse.json([
          { version: 2, isCurrent: true },
          { version: 1, isCurrent: false },
        ])
      ),
      http.get(`${BASE}/api/workflows/wf-1/versions/1`, () =>
        HttpResponse.json({ definition: { nodes: [], edges: [] } })
      ),
      http.post(`${BASE}/api/workflows/wf-1/rollback/1`, async ({ request }) => {
        rollbackSeen(await request.json());
        return HttpResponse.json({ ok: true });
      }),
    );

    renderModal([], [], { canRestore: true, onClose });
    await waitFor(() => expect(screen.getByText('Version 1')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Version 1'));
    fireEvent.click(await screen.findByRole('button', { name: /Restore v1/ }));

    await waitFor(() => expect(rollbackSeen).toHaveBeenCalledWith({ reason: 'Rolled back to v1 via diff viewer' }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closeButton_callsOnClose', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf-1/versions`, () => HttpResponse.json([])));
    const onClose = vi.fn();
    patchFetch();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <WorkflowDiffModal workflowId="wf-1" currentDefinition={{ nodes: [], edges: [] }} onClose={onClose} />
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByText('Workflow Diff')).toBeInTheDocument());
    fireEvent.click(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledOnce();
  });
});
