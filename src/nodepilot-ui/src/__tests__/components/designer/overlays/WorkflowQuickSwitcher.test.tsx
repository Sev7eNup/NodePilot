import { useEffect } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WorkflowQuickSwitcher } from '../../../../components/designer/overlays/WorkflowQuickSwitcher';
import { api } from '../../../../api/client';

/**
 * Pin: when the user picks a different workflow from inside an editor route, the
 * navigation carries `fromWorkflow` in location.state. Dirty confirmation is owned by
 * WorkflowEditorPage/useBlocker, so this component must not prompt or pre-write recents
 * before a blockable editor-to-editor navigation succeeds.
 */

vi.mock('../../../../api/client', () => ({
  api: { get: vi.fn() },
}));

const mockedGet = api.get as unknown as ReturnType<typeof vi.fn>;

type CapturedLocationState = { fromWorkflow?: { id: string; name: string } };
let capturedState: CapturedLocationState | null = null;
const getCapturedState = () => capturedState;

function LocationSpy() {
  const location = useLocation();
  // Capture in an effect, not during render — render-phase reassignment of an outer
  // binding is a side effect (react-hooks/globals). act() flushes this before assertions.
  useEffect(() => { capturedState = location.state as CapturedLocationState | null; }, [location.state]);
  return null;
}

function renderAtEditorRoute(currentId: string | null, opts: { isDirty?: boolean } = {}) {
  const initialEntries = currentId ? [`/workflows/${currentId}`] : ['/somewhere-else'];
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={initialEntries}>
        <Routes>
          <Route path="/workflows/:id" element={<WorkflowQuickSwitcher onClose={vi.fn()} isDirty={opts.isDirty} />} />
          <Route path="/somewhere-else" element={<WorkflowQuickSwitcher onClose={vi.fn()} isDirty={opts.isDirty} />} />
        </Routes>
        <LocationSpy />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('WorkflowQuickSwitcher - location.state fromWorkflow', () => {
  beforeEach(() => {
    localStorage.clear();
    mockedGet.mockReset();
    capturedState = null;
  });

  it('switchFromEditor_carriesFromWorkflowInState', async () => {
    mockedGet.mockResolvedValue([
      { id: 'wf-A', name: 'Alpha', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
      { id: 'wf-B', name: 'Beta', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
    ]);

    renderAtEditorRoute('wf-A');

    await screen.findByText('Beta');
    fireEvent.click(screen.getByText('Beta'));

    expect(getCapturedState()?.fromWorkflow).toEqual({ id: 'wf-A', name: 'Alpha' });
    expect(localStorage.getItem('nodepilot.recentWorkflows')).toBeNull();
  });

  it('sameWorkflowClick_noNavigation_updatesRecentOnly', async () => {
    mockedGet.mockResolvedValue([
      { id: 'wf-A', name: 'Alpha', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
    ]);

    renderAtEditorRoute('wf-A');
    await screen.findByText('Alpha');
    capturedState = null;
    fireEvent.click(screen.getByText('Alpha'));

    expect(getCapturedState()?.fromWorkflow).toBeUndefined();
    expect(localStorage.getItem('nodepilot.recentWorkflows')).toBe(JSON.stringify(['wf-A']));
  });

  it('dirtySwitch_defersConfirmationToPageBlocker_andDoesNotPrewriteRecents', async () => {
    mockedGet.mockResolvedValue([
      { id: 'wf-A', name: 'Alpha', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
      { id: 'wf-B', name: 'Beta', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
    ]);
    const confirmSpy = vi.spyOn(window, 'confirm');

    renderAtEditorRoute('wf-A', { isDirty: true });
    await screen.findByText('Beta');
    fireEvent.click(screen.getByText('Beta'));

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(getCapturedState()?.fromWorkflow).toEqual({ id: 'wf-A', name: 'Alpha' });
    expect(localStorage.getItem('nodepilot.recentWorkflows')).toBeNull();
    confirmSpy.mockRestore();
  });

  it('missingCurrentWorkflowName_usesCurrentIdAsTooltipFallback', async () => {
    mockedGet.mockResolvedValue([
      { id: 'wf-B', name: 'Beta', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
    ]);

    renderAtEditorRoute('wf-A', { isDirty: true });
    await screen.findByText('Beta');
    fireEvent.click(screen.getByText('Beta'));

    expect(getCapturedState()?.fromWorkflow).toEqual({ id: 'wf-A', name: 'wf-A' });
  });

  it('dirtyButSameWorkflowClick_noPrompt', async () => {
    mockedGet.mockResolvedValue([
      { id: 'wf-A', name: 'Alpha', description: null, definitionJson: '{}', version: 1, isEnabled: true, createdAt: '', updatedAt: '', createdBy: null, updatedBy: null },
    ]);
    const confirmSpy = vi.spyOn(window, 'confirm');

    renderAtEditorRoute('wf-A', { isDirty: true });
    await screen.findByText('Alpha');
    fireEvent.click(screen.getByText('Alpha'));

    expect(confirmSpy).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
  });
});
