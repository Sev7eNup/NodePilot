import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { MobileWorkflowView } from '../../pages/MobileWorkflowView';
import { NODE_SCALES } from '../../stores/designStore';
import { getNodeShape, getIconScaleMultiplier } from '../../components/designer/nodes/shapes';

const BASE = 'http://localhost';

// Mutable SignalR stub so a test can inject a live execution without a real connection.
const sig = vi.hoisted(() => ({ liveExecution: null as unknown }));
vi.mock('../../hooks/useSignalR', () => ({
  useWorkflowSignalR: () => ({ liveExecution: sig.liveExecution }),
}));

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const WORKFLOW = {
  id: 'wf1',
  name: 'Disk Health Check',
  description: '',
  isEnabled: true,
  version: 1,
  definitionJson: JSON.stringify({
    nodes: [
      { id: 'step-1', type: 'activity', position: { x: 80, y: 80 }, data: { label: 'Check Disk', activityType: 'runScript', config: { script: 'Get-PSDrive C' } } },
    ],
    edges: [],
  }),
};

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); sig.liveExecution = null; });
afterAll(() => server.close());

function renderView() {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <MobileWorkflowView workflowId="wf1" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('MobileWorkflowView', () => {
  it('renders the workflow name, a read-only hint, and the graph node', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf1`, () => HttpResponse.json(WORKFLOW)));
    renderView();

    await waitFor(() => expect(screen.getByText('Disk Health Check')).toBeInTheDocument());
    expect(screen.getByText(/read-only view/i)).toBeInTheDocument();
    // The reused ActivityNode renders the node label on the canvas.
    await waitFor(() => expect(screen.getByText('Check Disk')).toBeInTheDocument());
  });

  it('renders activity icons at the enlarged mobile scale (lg), not the desktop xs default', async () => {
    server.use(http.get(`${BASE}/api/workflows/wf1`, () => HttpResponse.json(WORKFLOW)));
    const { container } = renderView();

    await waitFor(() => expect(screen.getByText('Check Disk')).toBeInTheDocument());
    // MobileWorkflowView supplies NodeScaleOverrideContext = 3 (lg), so the reused ActivityNode
    // renders its icon at the lg scale rather than the xs default — the override is what makes the
    // phone graph legible without touching the global design store. runScript is a shaped node
    // (hexPointy); its Carbon icon renders at lg iconFont × the shape's iconScale × a modest growth
    // factor (see ActivityNode SHAPED_ICON_GROW), applied as the SVG width/height.
    const shape = getNodeShape('runScript');
    const iconScale = getIconScaleMultiplier(shape);
    const lgIconSize = String(Math.max(10, Math.round(NODE_SCALES[3].iconFont * iconScale * 1.35)));
    const icons = Array.from(container.querySelectorAll<SVGSVGElement>('svg'));
    expect(icons.some((el) => el.getAttribute('width') === lgIconSize)).toBe(true);
  });

  it('reflects live execution status without crashing', async () => {
    sig.liveExecution = { executionId: 'ex1', workflowId: 'wf1', status: 'Running', startedAt: '2026-06-01T00:00:00Z', steps: [{ executionId: 'ex1', stepId: 'step-1', status: 'Running' }] };
    server.use(http.get(`${BASE}/api/workflows/wf1`, () => HttpResponse.json(WORKFLOW)));
    renderView();

    // Graph still renders with the live status applied (node id === step id mapping).
    await waitFor(() => expect(screen.getByText('Check Disk')).toBeInTheDocument());
  });
});
