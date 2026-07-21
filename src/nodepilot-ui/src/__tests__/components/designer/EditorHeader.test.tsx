import { useEffect } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { EditorHeader } from '../../../components/designer/EditorHeader';
import type { Workflow } from '../../../types/api';
import { useDesignStore } from '../../../stores/designStore';
import { useThemeStore, THEMES } from '../../../stores/themeStore';

/**
 * EditorHeader back-button tests. The back button now uses navigate(-1) and reads
 * from location.state (fromWorkflow) for the tooltip. The dirty guard is handled
 * by useBlocker in the parent page, not the button itself.
 *
 * Pinned:
 *   - No fromWorkflow in state → generic "Back to workflow list" tooltip
 *   - fromWorkflow in state → tooltip names the previous workflow
 *   - fromWorkflow click calls browser-back behavior
 *   - missing fromWorkflow falls back to /workflows
 *   - Button is never disabled
 */


function defaultProps(over: Partial<Parameters<typeof EditorHeader>[0]> = {}) {
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
    isDirty: false,
    canWrite: true,
    nodes: [],
    aiChatOpen: false,
    onToggleAiChat: vi.fn(),
    undo: vi.fn(),
    redo: vi.fn(),
    historyPast: [],
    historyFuture: [],
    tidyLayout: vi.fn(),
    isTidying: false,
    layoutMode: 'LR' as const,
    restoreOrigLayout: vi.fn(),
    hasOrigLayout: false,
    setSearchOpen: vi.fn(),
    setFindReplaceOpen: vi.fn(),
    zoomToSelection: vi.fn(),
    setDiffOpen: vi.fn(),
    simulation: null,
    runSimulation: vi.fn(),
    clearSimulation: vi.fn(),
    lintResult: { errors: [], warnings: [] },
    setLintPanelOpen: vi.fn(),
    setHelpOpen: vi.fn(),
    hiddenActivityTypes: new Set<string>(),
    setHiddenActivityTypes: vi.fn(),
    liveExecution: null,
    handleRunClick: vi.fn(),
    exportPng: vi.fn(),
    onSave: vi.fn(),
    isPublishing: false,
    onRequestPublish: vi.fn(),
    roleCanWrite: true,
    isLockedByMe: true,
    isLockedByOther: false,
    onLock: vi.fn(),
    isLocking: false,
    onUnlock: vi.fn(),
    isUnlocking: false,
    onDisable: vi.fn(),
    isDisabling: false,
    isEnabling: false,
    ...over,
  };
}

let capturedPathname = '';
function LocationSpy() {
  const { pathname } = useLocation();
  // Capture in an effect, not during render — render-phase reassignment of an outer
  // binding is a side effect (react-hooks/globals). act() flushes this before assertions.
  useEffect(() => { capturedPathname = pathname; }, [pathname]);
  return null;
}

// Render with a MemoryRouter that provides location.state
function renderWithState(
  state: Record<string, unknown> | undefined,
  entries: Parameters<typeof MemoryRouter>[0]['initialEntries'] = [{ pathname: '/workflows/wf-current', state }],
  initialIndex?: number,
) {
  return render(
    <MemoryRouter initialEntries={entries} initialIndex={initialIndex}>
      <EditorHeader {...defaultProps()} />
      <LocationSpy />
    </MemoryRouter>,
  );
}

function getBackButton(): HTMLButtonElement {
  // The button always renders — title varies based on fromWorkflow state
  return screen.getByRole('button', { name: /^(Back to:|Back to workflow list)/i }) as HTMLButtonElement;
}

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

  it('standardMode_keepsCoreActionsAndMovesSecondaryActionsUnderMore', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    renderWithState(undefined);

    expect(screen.getByRole('button', { name: /Test/i })).toBeInTheDocument();
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
    // Overlays are inline buttons now, not behind the Eye popover; the compact "Display" and
    // "Overlays" popover triggers are gone.
    expect(screen.getByTestId('toggle-dataflow-overlay')).toBeInTheDocument();
    expect(screen.queryByTestId('view-overlays-trigger')).not.toBeInTheDocument();
    expect(screen.queryByTestId('canvas-settings-trigger')).not.toBeInTheDocument();
    // Run stays resolvable by its accessible name (icon-only Play, no "Run" label).
    expect(screen.getByRole('button', { name: 'Test run' })).toBeInTheDocument();
    expect(screen.getByTestId('toggle-toolbar-layout')).toBeInTheDocument();
  });
});

// Role / lifecycle affordances come from the SHARED RunControls + LifecycleControls, so they must
// behave identically in both layouts. Run the same contract against each.
describe.each(['compact', 'classic'] as const)('EditorHeader — role contract (%s layout)', (layout) => {
  beforeEach(() => {
    vi.restoreAllMocks();
    useDesignStore.setState({ designerMode: 'expert', toolbarLayout: layout });
  });

  it('writer_seesRunAndDisable', () => {
    // defaultProps: roleCanWrite true, workflow.isEnabled true → Run + Disable (PowerOff).
    renderWithState(undefined);
    expect(screen.getByRole('button', { name: 'Test run' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Disable' })).toBeInTheDocument();
  });

  it('viewer_seesNeitherRunNorLifecycle', () => {
    render(
      <MemoryRouter initialEntries={[{ pathname: '/workflows/wf-current' }]}>
        <EditorHeader {...defaultProps({ roleCanWrite: false, canWrite: false })} />
      </MemoryRouter>,
    );
    expect(screen.queryByRole('button', { name: 'Test run' })).not.toBeInTheDocument();
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
    // Options render in THEMES order → selecting one updates the global store and closes the popover.
    fireEvent.click(opts[1]);
    expect(useThemeStore.getState().theme).toBe(THEMES[1].id);
    expect(screen.queryAllByRole('menuitemradio')).toHaveLength(0);
  });
});
