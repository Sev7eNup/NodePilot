import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WatchTab } from '../../../../components/designer/execution/WatchTab';

// The globals dropdown runs a useQuery against /global-variables, so the client is stubbed to
// keep the test deterministic and offline. WatchTab only consumes api.get.
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

// The Watch tab persists user expressions to localStorage keyed by workflowId and restores
// them on mount. Covered here because the ExecutionPanel suite does not test it.
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
