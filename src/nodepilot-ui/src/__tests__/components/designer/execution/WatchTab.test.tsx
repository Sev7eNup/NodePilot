import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WatchTab } from '../../../../components/designer/execution/WatchTab';

// The globals dropdown fires a useQuery against /global-variables — stub the client so the
// test is deterministic and offline. WatchTab only consumes api.get.
vi.mock('../../../../api/client', () => ({
  api: { get: vi.fn(() => Promise.resolve([])) },
}));

function renderWatch(workflowId: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <WatchTab workflowId={workflowId} databus={{}} nodes={[]} />
    </QueryClientProvider>,
  );
}

// Characterization: the Watch tab persists user expressions to localStorage keyed by
// workflowId, and restores them on mount. Pinned here because the extraction moved this
// persistence out of ExecutionPanel; the existing ExecutionPanel suite does not cover it.
describe('WatchTab — expression persistence (characterization)', () => {
  beforeEach(() => localStorage.clear());

  it('persists an added expression to localStorage keyed by workflowId', () => {
    renderWatch('wf-persist');
    const input = screen.getByRole('textbox');
    fireEvent.change(input, { target: { value: 'step-a.output' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    const stored = JSON.parse(localStorage.getItem('nodepilot-watch-expressions:wf-persist') ?? '[]');
    expect(stored).toContain('step-a.output');
  });

  it('restores persisted expressions on mount', () => {
    localStorage.setItem('nodepilot-watch-expressions:wf-restore', JSON.stringify(['globals.FOO']));
    renderWatch('wf-restore');
    expect(screen.getByText('globals.FOO')).toBeInTheDocument();
  });
});
