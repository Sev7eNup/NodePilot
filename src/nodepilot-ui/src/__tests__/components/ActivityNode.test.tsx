import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, act, within } from '@testing-library/react';
import { ReactFlowProvider } from '@xyflow/react';
import { ActivityNode } from '../../components/designer/nodes/ActivityNode';
import { useDesignStore } from '../../stores/designStore';
import { usePointerFlowPosition } from '../../stores/pointerFlowPositionStore';

// Config summaries render only in the 'card' node style, so force it for these tests.
// Resetting nodeIconStyle and autoHidePorts and clearing the pointer store keeps glyph-view
// and port-reveal cases from leaking into later ones.
beforeEach(() => {
  useDesignStore.setState({ nodeStyle: 'card', nodeIconStyle: 'shape', autoHidePorts: true });
  usePointerFlowPosition.setState({ x: null, y: null });
});

function renderActivityNode(data: Record<string, unknown>, selected = false) {
  // ActivityNode expects NodeProps, but only data and selected matter here.
  const props = {
    id: 'test-node',
    data,
    selected,
    type: 'activity',
    isConnectable: true,
    zIndex: 0,
    positionAbsoluteX: 0,
    positionAbsoluteY: 0,
    dragging: false,
    deletable: true,
    selectable: true,
    parentId: undefined,
    sourcePosition: undefined,
    targetPosition: undefined,
    dragHandle: undefined,
    width: 200,
    height: 100,
    measured: { width: 200, height: 100 },
  } as any;

  return render(
    <ReactFlowProvider>
      <ActivityNode {...props} />
    </ReactFlowProvider>
  );
}

describe('ActivityNode', () => {
  it('renders correct label', () => {
    renderActivityNode({ label: 'Check Disk', activityType: 'runScript', config: {} });
    expect(screen.getByText('Check Disk')).toBeInTheDocument();
  });

  it('renders activity type as label when no label provided', () => {
    renderActivityNode({ activityType: 'delay', config: {} });
    expect(screen.getByText('delay')).toBeInTheDocument();
  });

  it('rendersFourPortHandles_allConnectableInBothDirections', () => {
    // Every side handle is both a connection start and a connection end. The JSON puts no
    // restriction on sourceHandle/targetHandle either.
    const { container } = renderActivityNode({ label: 'Ports', activityType: 'runScript', config: {} });

    expect(container.querySelectorAll('.react-flow__handle')).toHaveLength(4);
    for (const side of ['top', 'right', 'bottom', 'left']) {
      expect(container.querySelector(`[data-handleid="${side}"]`)).toHaveClass('connectablestart');
      expect(container.querySelector(`[data-handleid="${side}"]`)).toHaveClass('connectableend');
    }
  });

  it('displays config summary for runScript', () => {
    renderActivityNode({
      label: 'My Script',
      activityType: 'runScript',
      config: { script: 'Get-PSDrive C' },
    });
    expect(screen.getByText('Get-PSDrive C')).toBeInTheDocument();
  });

  it('displays config summary for delay', () => {
    renderActivityNode({
      label: 'Wait',
      activityType: 'delay',
      config: { seconds: 10 },
    });
    expect(screen.getByText('Wait 10 seconds')).toBeInTheDocument();
  });

  it('displays config summary for restApi', () => {
    renderActivityNode({
      label: 'API Call',
      activityType: 'restApi',
      config: { method: 'POST', url: 'https://api.example.com' },
    });
    // The restApi card renders the method in a badge span and the url separately.
    expect(screen.getByText('POST')).toBeInTheDocument();
    expect(screen.getByText('https://api.example.com')).toBeInTheDocument();
  });

  // The hover tooltip summary can carry long unbreakable tokens such as registry paths, file
  // paths and URLs. Without a word-break utility they overflow the fixed-width box, so the
  // rendered summary must preserve formatting (whitespace-pre-wrap) and break long words
  // (break-words).
  describe('hover tooltip wrapping', () => {
    it('wraps a long unbreakable registry path inside the tooltip box', () => {
      vi.useFakeTimers();
      try {
        const longPath =
          'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\Results\\Install';
        const { container } = renderActivityNode({
          label: 'reg: WU Install Results exists?',
          activityType: 'registryOperation',
          config: { operation: 'exists', keyPath: longPath },
        });

        fireEvent.mouseEnter(container.firstChild as Element);
        act(() => {
          vi.advanceTimersByTime(400); // hover-delay before the tooltip mounts
        });

        const portal = document.querySelector('.np-tooltip-portal');
        expect(portal).toBeTruthy();

        const summaryEl = within(portal as HTMLElement).getByText(`exists: ${longPath}`);
        expect(summaryEl.className).toContain('whitespace-pre-wrap');
        expect(summaryEl.className).toContain('break-words');
      } finally {
        vi.useRealTimers();
      }
    });
  });

  // Triggers, control-flow nodes and returnData each render through their own clip-path shape
  // instead of a plain square. These cases check that those paths mount without crashing and
  // still show the node label.
  describe('shape rendering (Triggers / Control Flow / returnData)', () => {
    it('mounts a trigger node (pennant path) without crashing', () => {
      renderActivityNode({ label: 'Schedule', activityType: 'scheduleTrigger', config: { cronExpression: '0 0 * * * ? *' } });
      expect(screen.getByText('Schedule')).toBeInTheDocument();
    });

    it('mounts a control-flow decision node (diamond path) without crashing', () => {
      renderActivityNode({ label: 'Branch', activityType: 'decision', config: {} });
      expect(screen.getByText('Branch')).toBeInTheDocument();
    });

    it('mounts a junction node (hexLong path) without crashing', () => {
      renderActivityNode({ label: 'Merge', activityType: 'junction', config: { mode: 'waitAll' } });
      expect(screen.getByText('Merge')).toBeInTheDocument();
    });

    it.each([
      ['forEach', 'reel'],
      ['startWorkflow', 'tagLeft'],
    ])('mounts the %s control node (%s path) without crashing', (activityType) => {
      renderActivityNode({ label: `c-${activityType}`, activityType, config: {} });
      expect(screen.getByText(`c-${activityType}`)).toBeInTheDocument();
    });

    it('mounts a returnData node (flag path) without crashing', () => {
      renderActivityNode({ label: 'Return', activityType: 'returnData', config: { data: { ok: true } } });
      expect(screen.getByText('Return')).toBeInTheDocument();
    });

    it('renders trigger in classic mode without crashing', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      renderActivityNode({ label: 'Manual', activityType: 'manualTrigger', config: {} });
      expect(screen.getByText('Manual')).toBeInTheDocument();
    });

    it('marks trigger nodes as workflow entry points in card mode', () => {
      const { container } = renderActivityNode({ label: 'Manual', activityType: 'manualTrigger', config: {} });

      expect(container.querySelector('[data-testid="entry-accent"]')).toBeInTheDocument();
      expect(container.querySelector('[data-testid="entry-badge"]')).toBeInTheDocument();
    });

    it('marks trigger nodes as workflow entry points in classic mode', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      const { container } = renderActivityNode({ label: 'Schedule', activityType: 'scheduleTrigger', config: { cronExpression: '0 0 * * * ? *' } });

      expect(container.querySelector('[data-testid="entry-accent"]')).toBeInTheDocument();
      expect(container.querySelector('[data-testid="entry-badge"]')).toBeInTheDocument();
    });

    it.each(['runScript', 'startWorkflow'])('does not mark %s as a workflow entry point', (activityType) => {
      const { container } = renderActivityNode({ label: `not-${activityType}`, activityType, config: {} });

      expect(container.querySelector('[data-testid="entry-accent"]')).not.toBeInTheDocument();
      expect(container.querySelector('[data-testid="entry-badge"]')).not.toBeInTheDocument();
    });

    it('does not show an active entry marker on disabled trigger nodes', () => {
      const { container } = renderActivityNode({ label: 'Disabled trigger', activityType: 'manualTrigger', config: {}, disabled: true });

      expect(container.querySelector('[data-testid="entry-accent"]')).not.toBeInTheDocument();
      expect(container.querySelector('[data-testid="entry-badge"]')).not.toBeInTheDocument();
    });

    // Each action renders through its own clipped shape. Mounting a representative spread
    // (card and classic) surfaces a broken polygon or shape-registry entry here.
    it.each([
      ['sql', 'cylinder'],
      ['restApi', 'browser'],
      ['runScript', 'hexPointy'],
      ['generateText', 'pillH'],
      ['delay', 'stopwatch'],
    ])('mounts the %s action node (%s shape) without crashing', (activityType) => {
      renderActivityNode({ label: `n-${activityType}`, activityType, config: {} });
      expect(screen.getByText(`n-${activityType}`)).toBeInTheDocument();
    });

    it('renders an action node in classic mode without crashing', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      renderActivityNode({ label: 'Query', activityType: 'sql', config: {} });
      expect(screen.getByText('Query')).toBeInTheDocument();
    });

    // A remote action drawn as a clipped shape must still show its machine-colour indicator
    // (data-testid="machine-stripe").
    it('renders the machine-colour indicator on a remote action shape (classic)', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      const { container } = renderActivityNode({
        label: 'Remote', activityType: 'runScript', config: {}, __machineColorIdx: 0,
      });
      expect(container.querySelector('[data-testid="machine-stripe"]')).toBeInTheDocument();
    });

    // Control-flow nodes carry the shared indigo accent layer; returnData, triggers and normal
    // actions do not.
    it('renders the control-group frame on control-flow nodes but not on others (classic)', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      for (const activityType of ['decision', 'junction', 'forEach', 'startWorkflow']) {
        const { container, unmount } = renderActivityNode({ label: `ctl-${activityType}`, activityType, config: {} });
        expect(container.querySelector('[data-testid="control-accent"]'), `${activityType} has frame`).toBeInTheDocument();
        unmount();
      }
      for (const activityType of ['returnData', 'manualTrigger', 'runScript']) {
        const { container, unmount } = renderActivityNode({ label: `non-${activityType}`, activityType, config: {} });
        expect(container.querySelector('[data-testid="control-accent"]'), `${activityType} has NO frame`).not.toBeInTheDocument();
        unmount();
      }
    });
  });

  // In the classic icon view the bare palette glyph replaces the clip-path silhouette. The
  // toggle is classic-only; card mode keeps its header, and ports, entry marker and badges stay.
  describe('classic icon view (bare glyph)', () => {
    it('renders the bare palette glyph and no clip-path silhouette', () => {
      useDesignStore.setState({ nodeStyle: 'classic', nodeIconStyle: 'glyph' });
      const { container } = renderActivityNode({ label: 'Icon', activityType: 'runScript', config: {} });
      expect(container.querySelector('[data-testid="glyph-icon"]')).toBeInTheDocument();
      expect(container.querySelector('.np-shape-wrap')).not.toBeInTheDocument();
    });

    it('renders the clip-path silhouette (not the bare glyph) in shape view', () => {
      useDesignStore.setState({ nodeStyle: 'classic', nodeIconStyle: 'shape' });
      const { container } = renderActivityNode({ label: 'Shape', activityType: 'runScript', config: {} });
      expect(container.querySelector('.np-shape-wrap')).toBeInTheDocument();
      expect(container.querySelector('[data-testid="glyph-icon"]')).not.toBeInTheDocument();
    });

    it('ignores icon view in card mode (classic-only)', () => {
      useDesignStore.setState({ nodeStyle: 'card', nodeIconStyle: 'glyph' });
      const { container } = renderActivityNode({ label: 'Card', activityType: 'runScript', config: {} });
      expect(container.querySelector('[data-testid="glyph-icon"]')).not.toBeInTheDocument();
    });

    it('still marks enabled trigger nodes as entry points in icon view', () => {
      useDesignStore.setState({ nodeStyle: 'classic', nodeIconStyle: 'glyph' });
      const { container } = renderActivityNode({ label: 'Manual', activityType: 'manualTrigger', config: {} });
      expect(container.querySelector('[data-testid="glyph-icon"]')).toBeInTheDocument();
      expect(container.querySelector('[data-testid="entry-badge"]')).toBeInTheDocument();
    });

    it('renders the failure-heatmap glow behind the bare glyph in icon view', () => {
      // The glyph view has no box or silhouette, so the failure heatmap needs its own glow
      // layer to be visible at all.
      useDesignStore.setState({ nodeStyle: 'classic', nodeIconStyle: 'glyph' });
      const { container } = renderActivityNode({ label: 'Hot', activityType: 'runScript', config: {}, __failureTint: 0.8 });
      const glow = container.querySelector('[data-testid="heatmap-glow"]') as HTMLElement | null;
      expect(glow).toBeInTheDocument();
      // Uses the semantic error token so it stays correct across skins, never a hardcoded red.
      expect(glow?.style.backgroundColor).toContain('var(--color-error)');
    });

    it('does not render the heatmap glow when the node has a live status (live wins)', () => {
      useDesignStore.setState({ nodeStyle: 'classic', nodeIconStyle: 'glyph' });
      const { container } = renderActivityNode({ label: 'Live', activityType: 'runScript', config: {}, __failureTint: 0.8, __liveStatus: 'Running' });
      expect(container.querySelector('[data-testid="heatmap-glow"]')).not.toBeInTheDocument();
    });
  });

  // The health sparkline shows the recent outcomes of this step as small dots. It is a second
  // information layer next to the live-status colouring, so a pinned run must not hide it.
  // Only a dry-run simulation suppresses it.
  describe('health sparkline', () => {
    const health = [{ status: 'Succeeded' }, { status: 'Failed' }, { status: 'Succeeded' }];
    const dots = (c: HTMLElement) => ({
      green: c.querySelectorAll('div.bg-green-400').length,
      red: c.querySelectorAll('div.bg-red-400').length,
    });

    it('renders the outcome dots on an idle node', () => {
      const { container } = renderActivityNode({ label: 'Idle', activityType: 'runScript', config: {}, __health: health });
      expect(dots(container)).toEqual({ green: 2, red: 1 });
    });

    it('keeps the dots while a finished run is pinned on the canvas', () => {
      const { container } = renderActivityNode({ label: 'Pinned', activityType: 'runScript', config: {}, __health: health, __liveStatus: 'Succeeded' });
      expect(dots(container)).toEqual({ green: 2, red: 1 });
    });

    it('keeps the dots while the step is running', () => {
      const { container } = renderActivityNode({ label: 'Running', activityType: 'runScript', config: {}, __health: health, __liveStatus: 'Running' });
      expect(dots(container)).toEqual({ green: 2, red: 1 });
    });

    it('keeps the dots during a live run in classic style too', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      const { container } = renderActivityNode({ label: 'Classic', activityType: 'runScript', config: {}, __health: health, __liveStatus: 'Succeeded' });
      expect(dots(container)).toEqual({ green: 2, red: 1 });
    });

    it('suppresses the dots during a dry-run simulation', () => {
      const { container } = renderActivityNode({ label: 'Sim', activityType: 'runScript', config: {}, __health: health, __simulated: 'reachable' });
      expect(dots(container)).toEqual({ green: 0, red: 0 });
    });
  });

  // With autoHidePorts on, the active port handles render at opacity 0 until the node is selected
  // or hovered, the cursor is near it (pointer store), or a connection is dragged toward it. The
  // handles stay in the DOM and connectable; only opacity changes.
  describe('port auto-hide (reveal on proximity/selection)', () => {
    const leftOpacity = (c: HTMLElement) =>
      (c.querySelector('[data-handleid="left"]') as HTMLElement | null)?.style.opacity;

    it('hides active ports by default (auto-hide on, not near / selected)', () => {
      const { container } = renderActivityNode({ label: 'Hidden', activityType: 'runScript', config: {} });
      expect(leftOpacity(container)).toBe('0');
    });

    it('reveals ports when the node is selected', () => {
      const { container } = renderActivityNode({ label: 'Sel', activityType: 'runScript', config: {} }, true);
      expect(leftOpacity(container)).toBe('1');
    });

    it('reveals ports when the cursor is near the node (pointer store within radius)', () => {
      // The node rect here is (0,0,200,100), so a cursor at (50,50) sits inside the radius.
      usePointerFlowPosition.setState({ x: 50, y: 50 });
      const { container } = renderActivityNode({ label: 'Near', activityType: 'runScript', config: {} });
      expect(leftOpacity(container)).toBe('1');
    });

    it('keeps ports hidden when the cursor is far away', () => {
      usePointerFlowPosition.setState({ x: 5000, y: 5000 });
      const { container } = renderActivityNode({ label: 'Far', activityType: 'runScript', config: {} });
      expect(leftOpacity(container)).toBe('0');
    });

    it('always shows ports when auto-hide is off', () => {
      useDesignStore.setState({ autoHidePorts: false });
      const { container } = renderActivityNode({ label: 'Shown', activityType: 'runScript', config: {} });
      expect(leftOpacity(container)).toBe('1');
    });
  });

  describe('scheduleTrigger countdown vs paused state', () => {
    it('renders an upcoming-fire countdown when the workflow is enabled', () => {
      renderActivityNode({
        label: 'Every hour',
        activityType: 'scheduleTrigger',
        config: { cronExpression: '0 0 * * * ? *' },
      });
      // Card mode renders "Next: in Xm Ys"; relativeFromNow returns "in ..." or "now".
      const countdown = screen.queryByText(/Next: (in |now)/);
      expect(countdown).toBeTruthy();
      expect(screen.queryByText('Paused')).toBeNull();
    });

    it('renders "Paused" instead of the countdown when __workflowEnabled is false', () => {
      renderActivityNode({
        label: 'Every hour',
        activityType: 'scheduleTrigger',
        config: { cronExpression: '0 0 * * * ? *' },
        __workflowEnabled: false,
      });
      expect(screen.getByText('Paused')).toBeInTheDocument();
      expect(screen.queryByText(/^in /)).toBeNull();
    });

    it('renders "⏸ Paused" in classic mode when __workflowEnabled is false', () => {
      useDesignStore.setState({ nodeStyle: 'classic' });
      renderActivityNode({
        label: 'Every hour',
        activityType: 'scheduleTrigger',
        config: { cronExpression: '0 0 * * * ? *' },
        __workflowEnabled: false,
      });
      expect(screen.getByText('⏸ Paused')).toBeInTheDocument();
    });
  });
});
