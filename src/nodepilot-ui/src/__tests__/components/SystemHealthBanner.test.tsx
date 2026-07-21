import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SystemHealthBanner } from '../../components/dashboard/SystemHealthBanner';

function renderBanner(props: {
  clusterRole?: string | null;
  databaseProvider?: string;
  heartbeats?: Parameters<typeof SystemHealthBanner>[0]['heartbeats'];
  llmEnabled: boolean;
}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <SystemHealthBanner
          clusterRole={props.clusterRole ?? null}
          databaseProvider={props.databaseProvider ?? 'postgres'}
          heartbeats={props.heartbeats ?? []}
          llmEnabled={props.llmEnabled}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.restoreAllMocks());

describe('SystemHealthBanner — AI / LLM indicator', () => {
  it('shows "AI activated" when the LLM master switch is on', () => {
    // Default role is Viewer → maintenance/alert queries stay disabled, no fetch needed.
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderBanner({ llmEnabled: true });
    expect(screen.getByText('AI activated')).toBeInTheDocument();
    expect(screen.queryByText('AI disabled')).not.toBeInTheDocument();
  });

  it('shows "AI disabled" when the LLM master switch is off', () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
    renderBanner({ llmEnabled: false });
    expect(screen.getByText('AI disabled')).toBeInTheDocument();
    expect(screen.queryByText('AI activated')).not.toBeInTheDocument();
  });
});