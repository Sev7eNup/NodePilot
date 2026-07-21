import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WorkflowBrowser, WORKFLOW_LIST_HEIGHT_PX } from '../../../components/designer/WorkflowBrowser';
import { useWorkflowBrowserStore } from '../../../stores/workflowBrowserStore';
import { api } from '../../../api/client';
import type { Workflow } from '../../../types/api';

vi.mock('../../../api/client', () => ({
  api: { get: vi.fn() },
}));

const mockedGet = api.get as unknown as ReturnType<typeof vi.fn>;

function wf(id: string, name: string): Workflow {
  return {
    id, name, description: null, definitionJson: '{}', version: 1, isEnabled: true,
    createdAt: '2026-06-20T10:00:00Z', updatedAt: '2026-06-20T10:00:00Z',
    createdBy: null, updatedBy: null, activityCount: 3, triggerTypes: [],
  } as Workflow;
}

function renderBrowser(currentId: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <WorkflowBrowser currentWorkflowId={currentId} canEmbed={false} onOpen={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe('WorkflowBrowser info card', () => {
  beforeEach(() => {
    localStorage.clear();
    mockedGet.mockReset();
    // Trigger view → no folder tree (no shared-folders fetch). Expand the "no trigger" group
    // so its items render and can be hovered.
    useWorkflowBrowserStore.setState({ viewMode: 'trigger', collapsedFolders: { __none__: false }, infoCardHeight: 200 });
  });

  it('noHover_cardFallsBackToCurrentWorkflow', async () => {
    mockedGet.mockResolvedValue([wf('wf-A', 'Alpha'), wf('wf-B', 'Beta')]);
    renderBrowser('wf-B');
    await screen.findByRole('button', { name: /Alpha/ });

    const card = screen.getByTestId('workflow-info-card');
    expect(within(card).getByText('Beta')).toBeInTheDocument();
  });

  it('hoverItem_updatesCard_andLeaveRevertsToCurrent', async () => {
    mockedGet.mockResolvedValue([wf('wf-A', 'Alpha'), wf('wf-B', 'Beta')]);
    renderBrowser('wf-B');
    const alphaBtn = await screen.findByRole('button', { name: /Alpha/ });
    const card = screen.getByTestId('workflow-info-card');

    fireEvent.mouseEnter(alphaBtn);
    expect(within(card).getByText('Alpha')).toBeInTheDocument();

    fireEvent.mouseLeave(alphaBtn);
    expect(within(card).getByText('Beta')).toBeInTheDocument();
  });

  it('hoverCurrentItem_updatesCard_viaIsCurrentBranch', async () => {
    mockedGet.mockResolvedValue([wf('wf-A', 'Alpha'), wf('wf-B', 'Beta')]);
    renderBrowser('wf-B');
    const alphaBtn = await screen.findByRole('button', { name: /Alpha/ });
    const card = screen.getByTestId('workflow-info-card');

    // Move the card off the fallback first…
    fireEvent.mouseEnter(alphaBtn);
    expect(within(card).getByText('Alpha')).toBeInTheDocument();

    // …then hover the currently-open workflow's marker (a separate render branch with its
    // own mouse handlers). Its title is a hardcoded string in the component.
    const currentMarker = screen.getByTitle('Aktuell geöffneter Workflow');
    fireEvent.mouseEnter(currentMarker);
    expect(within(card).getByText('Beta')).toBeInTheDocument();
  });

  it('folderView_listSizedForExactly10Rows', () => {
    // In folder view the list has a fixed height for exactly 10 rows; the folder tree above
    // absorbs the remaining space.
    mockedGet.mockImplementation((url: string) =>
      Promise.resolve(url === '/workflows' ? [wf('wf-A', 'Alpha')] : []));
    useWorkflowBrowserStore.setState({ viewMode: 'folder', collapsedFolders: {}, infoCardHeight: 320 });
    renderBrowser('wf-A');
    expect(screen.getByTestId('workflow-list')).toHaveStyle({ height: `${WORKFLOW_LIST_HEIGHT_PX}px` });
  });
});
