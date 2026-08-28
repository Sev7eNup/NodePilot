import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WorkflowBrowser, FOLDER_TREE_MAX_HEIGHT_PX } from '../../../components/designer/WorkflowBrowser';
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
    // Trigger view has no folder tree and so no shared-folders fetch. Expand the group of
    // trigger-less workflows so its items render and can be hovered.
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

    // Move the card off the fallback first.
    fireEvent.mouseEnter(alphaBtn);
    expect(within(card).getByText('Alpha')).toBeInTheDocument();

    // Then hover the currently open workflow's marker, a separate render branch with its own
    // mouse handlers. Its title is a hardcoded string in the component.
    const currentMarker = screen.getByTitle('Aktuell geöffneter Workflow');
    fireEvent.mouseEnter(currentMarker);
    expect(within(card).getByText('Beta')).toBeInTheDocument();
  });

  it('folderView_treeHugsContent_listTakesRemainingSpace', () => {
    // The tree is height-capped and shrink-0 while the list flex-fills, so both blocks stay
    // pinned to the top of the sidebar.
    mockedGet.mockImplementation((url: string) =>
      Promise.resolve(url === '/workflows' ? [wf('wf-A', 'Alpha')] : []));
    useWorkflowBrowserStore.setState({ viewMode: 'folder', collapsedFolders: {}, infoCardHeight: 320 });
    renderBrowser('wf-A');

    const tree = screen.getByTestId('workflow-folder-tree');
    expect(tree).toHaveStyle({ maxHeight: `${FOLDER_TREE_MAX_HEIGHT_PX}px` });
    expect(tree.className).toContain('shrink-0');

    const list = screen.getByTestId('workflow-list');
    expect(list.className).toContain('flex-1');
    expect(list.style.height).toBe('');
  });

  it('triggerView_listStillFlexFills', () => {
    mockedGet.mockResolvedValue([wf('wf-A', 'Alpha')]);
    renderBrowser('wf-A');
    expect(screen.queryByTestId('workflow-folder-tree')).not.toBeInTheDocument();
    expect(screen.getByTestId('workflow-list').className).toContain('flex-1');
  });
});
