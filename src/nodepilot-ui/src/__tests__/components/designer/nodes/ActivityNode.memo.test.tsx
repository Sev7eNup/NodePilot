import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { ReactFlowProvider, type NodeProps } from '@xyflow/react';

/**
 * ActivityNode is the most expensive thing the canvas repeats per node, so it is memoized.
 * The memo has to survive the canvas projection, which rebuilds every node's `data` object on
 * every pass — a reference check on `data` would report a change every time and the memo would
 * do nothing.
 *
 * usePointerFlowPosition is called exactly once per render of the component body, so the mock
 * below doubles as a render counter.
 */

const mocks = vi.hoisted(() => ({ renders: 0 }));

vi.mock('../../../../stores/pointerFlowPositionStore', async () => {
  const actual = await vi.importActual<typeof import('../../../../stores/pointerFlowPositionStore')>(
    '../../../../stores/pointerFlowPositionStore',
  );
  return {
    ...actual,
    usePointerFlowPosition: () => {
      mocks.renders += 1;
      return false;
    },
  };
});

import { ActivityNode } from '../../../../components/designer/nodes/ActivityNode';

const baseProps = {
  id: 'step-1',
  type: 'activity',
  selected: false,
  isConnectable: true,
  positionAbsoluteX: 0,
  positionAbsoluteY: 0,
  width: 140,
  height: 132,
  dragging: false,
  zIndex: 0,
  deletable: true,
  selectable: true,
  draggable: true,
} as unknown as NodeProps;

const CONFIG = { script: 'Get-Date' };

/** The data object as the projection builds it: same values, new object. */
function projectedData() {
  return { label: 'Check Disk', activityType: 'runScript', config: CONFIG, inDegreeCount: 1 };
}

/** The node's ports need a React Flow store above them. */
function renderNode(props: Partial<NodeProps> = {}) {
  const ui = (overrides: Partial<NodeProps>) => (
    <ReactFlowProvider>
      <ActivityNode {...baseProps} data={projectedData()} {...overrides} />
    </ReactFlowProvider>
  );
  const { rerender } = render(ui(props));
  return { rerender: (next: Partial<NodeProps> = {}) => rerender(ui(next)) };
}

describe('ActivityNode — memoization', () => {
  beforeEach(() => { mocks.renders = 0; });

  it('does not re-render when data is rebuilt with the same entries', () => {
    const { rerender } = renderNode();
    const afterFirst = mocks.renders;

    rerender();
    rerender();

    expect(mocks.renders).toBe(afterFirst);
  });

  it('re-renders when a data value changes', () => {
    const { rerender } = renderNode();
    const afterFirst = mocks.renders;

    rerender({ data: { ...projectedData(), label: 'Renamed' } });

    expect(mocks.renders).toBeGreaterThan(afterFirst);
  });

  it('re-renders when a projected marker appears', () => {
    // Lint counts and live status arrive as extra keys on the same data object.
    const { rerender } = renderNode();
    const afterFirst = mocks.renders;

    rerender({ data: { ...projectedData(), __lintErrors: 1 } });

    expect(mocks.renders).toBeGreaterThan(afterFirst);
  });

  it('re-renders when the node is selected', () => {
    const { rerender } = renderNode();
    const afterFirst = mocks.renders;

    rerender({ selected: true });

    expect(mocks.renders).toBeGreaterThan(afterFirst);
  });

  it('re-renders when the node moves, so port proximity is computed against the new rect', () => {
    const { rerender } = renderNode();
    const afterFirst = mocks.renders;

    rerender({ positionAbsoluteX: 320 });

    expect(mocks.renders).toBeGreaterThan(afterFirst);
  });
});
