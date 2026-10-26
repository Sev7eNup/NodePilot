import { useEffect } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EditorHeader, type EditorHeaderProps } from '../../../components/designer/EditorHeader';
import type { EditorLifecycle } from '../../../hooks/useEditorLifecycle';
import type { Workflow } from '../../../types/api';
import type { KnowledgeCapabilities } from '../../../api/ai';
import { useDesignStore } from '../../../stores/designStore';
import { useThemeStore, THEMES } from '../../../stores/themeStore';

/**
 * EditorHeader back-button tests. The back button calls navigate(-1) and reads fromWorkflow
 * from location.state for its tooltip. The dirty guard lives in useBlocker on the parent
 * page, not in the button.
 *
 * Pinned:
 *   - without fromWorkflow the tooltip reads "Back to workflow list"
 *   - with fromWorkflow the tooltip names the previous workflow
 *   - a click with fromWorkflow goes back in history
 *   - a click without fromWorkflow falls back to /workflows
 *   - the button is never disabled
 */


/** Header props with lock-by-me defaults; `over` patches the lifecycle flags. */
function defaultProps(over: Partial<EditorLifecycle> = {}): EditorHeaderProps {
  const workflow: Workflow = {
    id: 'wf-current',
    name: 'Current Workflow',
    description: null,
    definitionJson: '{}',
    version: 1,
    isEnabled: true,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    createdBy: null,
    updatedBy: null,
  };
  return {
    workflowId: 'wf-current',
    workflow,
    name: 'Current Workflow',
    onRename: vi.fn(),
    nodes: [],
    lifecycle: {
      roleCanWrite: true,
      canWrite: true,
      isAdmin: false,
      isDirty: false,
      isSaving: false,
      isLockedByMe: true,
      isLockedByOther: false,
      isLocking: false,
      isUnlocking: false,
      isPublishing: false,
      isEnabling: false,
      isDisabling: false,
      isForceUnlocking: false,
      save: vi.fn(),
      lock: vi.fn(),
      unlock: vi.fn(),
      replaceDraft: vi.fn(),
      forceUnlock: vi.fn(),
      disable: vi.fn(),
      requestPublish: vi.fn(),
      ...over,
    },
    history: { undo: vi.fn(), redo: vi.fn(), historyPast: [], historyFuture: [] },
    layout: {
      tidyLayout: vi.fn(),
      isTidying: false,
      layoutMode: 'LR' as const,
      restoreOrigLayout: vi.fn(),
      hasOrigLayout: false,
    },
    overlays: {
      setSearchOpen: vi.fn(),
      setFindReplaceOpen: vi.fn(),
      setDiffOpen: vi.fn(),
      setLintPanelOpen: vi.fn(),
      setHelpOpen: vi.fn(),
    },
    simulation: { simulation: null, runSimulation: vi.fn(), clearSimulation: vi.fn() },
    run: { liveExecution: null, run: vi.fn(), cancelRun: vi.fn() },
    lintResult: { errors: [], warnings: [] },
    hiddenActivityTypes: new Set<string>(),
    setHiddenActivityTypes: vi.fn(),
    zoomToSelection: vi.fn(),
    exportPng: vi.fn(),
    aiChatOpen: false,
    onToggleAiChat: vi.fn(),
  };
}

let capturedPathname = '';
function LocationSpy() {
  const { pathname } = useLocation();
  // Capture in an effect, not during render: reassigning an outer binding while rendering is
  // a side effect (react-hooks/globals). act() flushes the effect before the assertions run.
  useEffect(() => { capturedPathname = pathname; }, [pathname]);
  return null;
}

const defaultCaps: KnowledgeCapabilities = {
  enabled: false, llm: true, docs: false, operational: false, sourceCode: false, db: false,
};

// EditorIdentity gates the AI-assistant button on the shared capabilities query. Seeding the
// cache with fresh data avoids a fetch at mount and keeps these tests deterministic without
// MSW; the default `llm: true` keeps the assistant button visible for the other tests.
function makeClient(caps: KnowledgeCapabilities | null = defaultCaps) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (caps) qc.setQueryData(['ai-knowledge-capabilities'], caps);
  return qc;
}

// Render inside a MemoryRouter that provides location.state.
function renderWithState(
  state: Record<string, unknown> | undefined,
  entries: Parameters<typeof MemoryRouter>[0]['initialEntries'] = [{ pathname: '/workflows/wf-current', state }],
  initialIndex?: number,
  caps: KnowledgeCapabilities | null = defaultCaps,
) {
  return render(
    <QueryClientProvider client={makeClient(caps)}>
      <MemoryRouter initialEntries={entries} initialIndex={initialIndex}>
        <EditorHeader {...defaultProps()} />
        <LocationSpy />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function getBackButton(): HTMLButtonElement {
  // The arrow always renders first; only its title varies with the fromWorkflow state. With
  // fromWorkflow a second "Back to workflow list" button follows it.
  return screen.getAllByRole('button', { name: /^(Back to:|Back to workflow list)/i })[0] as HTMLButtonElement;
}

const listButtons = () => screen.queryAllByRole('button', { name: /^Back to workflow list$/i });

describe('EditorHeader — designer back button (location.state)', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    capturedPathname = '';
    useDesignStore.setState({ designerMode: 'expert' });
  });

  it('noFromWorkflow_genericTooltip', () => {
    renderWithState(undefined);
    const btn = getBackButton();
    expect(btn).toHaveAttribute('title', 'Back to workflow list');
    expect(btn).not.toBeDisabled();
  });

  it('fromWorkflow_tooltipNamesPrevious', () => {
    renderWithState({ fromWorkflow: { id: 'wf-prev', name: 'Previous Workflow' } });
    const btn = getBackButton();
    expect(btn).toHaveAttribute('title', 'Back to: Previous Workflow');
    expect(btn).not.toBeDisabled();
  });

  it('clickWithFromWorkflow_navigatesBack', () => {
    renderWithState(
      { fromWorkflow: { id: 'wf-prev', name: 'Previous Workflow' } },
      [
        { pathname: '/workflows/wf-prev' },
        { pathname: '/workflows/wf-current', state: { fromWorkflow: { id: 'wf-prev', name: 'Previous Workflow' } } },
      ],
      1,
    );
    const btn = getBackButton();
    fireEvent.click(btn);
    expect(capturedPathname).toBe('/workflows/wf-prev');
  });

  it('clickWithoutFromWorkflow_navigatesToWorkflowList', () => {
    renderWithState(undefined);
    fireEvent.click(getBackButton());
    expect(capturedPathname).toBe('/workflows');
  });

  it('dependencyEntry_returnsToTheOriginalHistoryEntry', () => {
    renderWithState(
      { fromDependencies: true },
      [
        { pathname: '/dependencies', search: '?kind=machine&id=host&view=map&related=workflow:wf-current' },
        { pathname: '/workflows/wf-current', state: { fromDependencies: true } },
      ], 1,
    );
    const back = screen.getByRole('button', { name: 'Back to dependencies' });
    expect(back).not.toBeDisabled();
    fireEvent.click(back);
    expect(capturedPathname).toBe('/dependencies');
  });

  it('withoutFromWorkflow_rendersOnlyTheArrow', () => {
    renderWithState(undefined);
    // The arrow itself is titled "Back to workflow list"; no second button exists.
    expect(listButtons()).toHaveLength(1);
    expect(listButtons()[0]).toBe(getBackButton());
  });

  it('fromWorkflow_listButtonGoesStraightToWorkflowList', () => {
    renderWithState(
      { fromWorkflow: { id: 'wf-prev', name: 'Previous Workflow' } },
      [
        { pathname: '/workflows/wf-first' },
        { pathname: '/workflows/wf-prev', state: { fromWorkflow: { id: 'wf-first', name: 'First' } } },
        { pathname: '/workflows/wf-current', state: { fromWorkflow: { id: 'wf-prev', name: 'Previous Workflow' } } },
      ],
      2,
    );
    expect(getBackButton()).toHaveAttribute('title', 'Back to: Previous Workflow');
    expect(listButtons()).toHaveLength(1);

    fireEvent.click(listButtons()[0]);

    expect(capturedPathname).toBe('/workflows');
  });

  it('standardMode_keepsCoreActionsAndMovesSecondaryActionsUnderMore', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    renderWithState(undefined);

    expect(screen.getByRole('button', { name: /^Run$/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Debug/i })).not.toBeInTheDocument();
    expect(screen.queryByTitle(/Failure heatmap/i)).not.toBeInTheDocument();

    fireEvent.click(screen.getByTitle('More designer actions'));
    expect(screen.getByText('Export as JSON')).toBeInTheDocument();
    expect(screen.getByText('Keyboard shortcuts (?)')).toBeInTheDocument();
  });

  it('modeToggle_updatesThePersistedDesignerPreference', () => {
    renderWithState(undefined);
    fireEvent.click(screen.getByRole('button', { name: 'Standard' }));
    expect(useDesignStore.getState().designerMode).toBe('standard');
  });
});

describe('EditorHeader — toolbar-layout toggle', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    useDesignStore.setState({ designerMode: 'expert', toolbarLayout: 'compact' });
  });

  it('toggle_switchesThePersistedLayout', () => {
    renderWithState(undefined);
    expect(useDesignStore.getState().toolbarLayout).toBe('compact');
    fireEvent.click(screen.getByTestId('toggle-toolbar-layout'));
    expect(useDesignStore.getState().toolbarLayout).toBe('classic');
    fireEvent.click(screen.getByTestId('toggle-toolbar-layout'));
    expect(useDesignStore.getState().toolbarLayout).toBe('compact');
  });

  it('classic_unfoldsThePopoversIntoInlineControls', () => {
    useDesignStore.setState({ toolbarLayout: 'classic' });
    renderWithState(undefined);
    // In the classic layout the overlay toggles are inline buttons, so the compact "Display"
    // and "Overlays" popover triggers are absent.
    expect(screen.getByTestId('toggle-dataflow-overlay')).toBeInTheDocument();
    expect(screen.queryByTestId('view-overlays-trigger')).not.toBeInTheDocument();
    expect(screen.queryByTestId('canvas-settings-trigger')).not.toBeInTheDocument();
    // The run button is icon-only, so it is resolved by its accessible name.
    expect(screen.getByRole('button', { name: 'Run' })).toBeInTheDocument();
    expect(screen.getByTestId('toggle-toolbar-layout')).toBeInTheDocument();
  });
});

// Role and lifecycle affordances come from the shared RunControls and LifecycleControls, so they
// must behave the same in both layouts. The same contract runs against each.
describe.each(['compact', 'classic'] as const)('EditorHeader — role contract (%s layout)', (layout) => {
  beforeEach(() => {
    vi.restoreAllMocks();
    useDesignStore.setState({ designerMode: 'expert', toolbarLayout: layout });
  });

  it('writer_seesRunAndDisable', () => {
    // defaultProps sets roleCanWrite and workflow.isEnabled, so Run and Disable are shown.
    renderWithState(undefined);
    expect(screen.getByRole('button', { name: 'Run' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Disable' })).toBeInTheDocument();
  });

  it('viewer_seesNeitherRunNorLifecycle', () => {
    render(
      <QueryClientProvider client={makeClient()}>
        <MemoryRouter initialEntries={[{ pathname: '/workflows/wf-current' }]}>
          <EditorHeader {...defaultProps({ roleCanWrite: false, canWrite: false })} />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(screen.queryByRole('button', { name: 'Run' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Disable' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Publish' })).not.toBeInTheDocument();
  });
});

describe('EditorHeader — color-skin switcher', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    useDesignStore.setState({ designerMode: 'expert' });
    useThemeStore.setState({ theme: 'light' });
  });

  it('skinToggle_isIconOnly_opensPopover_andSwitchesSkin', () => {
    const { container } = renderWithState(undefined);
    const header = container.querySelector('.np-editor-header');
    expect(header).toHaveClass('relative');
    expect(header).toHaveClass('z-[45]');
    expect(header).not.toHaveClass('z-20');

    const toggle = screen.getByTestId('toggle-skin');
    // Icon-only trigger: no visible text label until clicked.
    expect(toggle.textContent).toBe('');
    // Popover is closed initially.
    expect(screen.queryAllByRole('menuitemradio')).toHaveLength(0);
    // Opening lists every skin from THEMES plus `system`.
    fireEvent.click(toggle);
    const menu = screen.getByRole('menu');
    expect(menu).toHaveClass('z-50');
    expect(menu).not.toHaveClass('z-30');
    const opts = screen.getAllByRole('menuitemradio');
    expect(opts).toHaveLength(THEMES.length + 1); // every skin + `system`
    // Options render in THEMES order; selecting one updates the store and closes the popover.
    fireEvent.click(opts[1]);
    expect(useThemeStore.getState().theme).toBe(THEMES[1].id);
    expect(screen.queryAllByRole('menuitemradio')).toHaveLength(0);
  });
});

describe('EditorHeader — AI assistant visibility (capabilities.llm)', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    useDesignStore.setState({ designerMode: 'expert' });
  });

  it('llmUsable_rendersTheAssistantToggle', () => {
    renderWithState(undefined);
    expect(screen.getByTestId('toggle-ai-assistant')).toBeInTheDocument();
  });

  it('llmNotUsable_hidesTheAssistantToggle', () => {
    renderWithState(undefined, undefined, undefined, { ...defaultCaps, llm: false });
    expect(screen.queryByTestId('toggle-ai-assistant')).not.toBeInTheDocument();
  });

  it('capabilitiesNotLoaded_hidesTheAssistantToggle', () => {
    // An empty cache means the query is loading or failed, so the toggle stays hidden.
    renderWithState(undefined, undefined, undefined, null);
    expect(screen.queryByTestId('toggle-ai-assistant')).not.toBeInTheDocument();
  });
});
