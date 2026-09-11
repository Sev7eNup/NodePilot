import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { Position } from '@xyflow/react';

/**
 * Path geometry is the most expensive thing an edge does: with the premium canvas on, the
 * curve behind the arrowhead is trimmed by a binary search over the cubic's arc length. The
 * canvas re-renders on every frame of a node drag, so an edge that nothing touched must not
 * redo that work — and must not re-render at all.
 */

const mocks = vi.hoisted(() => ({
  designState: {
    nodeScaleIndex: 1,
    labelFontOffsetIndex: 2,
    edgeWidthIndex: 1,
    edgeRouting: 'smart',
    edgesAnimated: false,
    premiumCanvas: true,
  },
  getSmartEdgePath: vi.fn(() => [['M0,0 C50,0 50,0 100,0', 50, 0]] as unknown[]),
  /** Bumped once per render of the component body — useStore is only called from there. */
  useStoreCalls: 0,
}));

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual<typeof import('@xyflow/react')>('@xyflow/react');
  return {
    ...actual,
    BaseEdge: ({ path }: { path: string }) => <path data-testid="base-edge" d={path} />,
    EdgeLabelRenderer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    useStore: vi.fn((selector: (s: unknown) => unknown) => {
      mocks.useStoreCalls += 1;
      return selector({ nodeLookup: new Map() });
    }),
  };
});

vi.mock('../../../../stores/designStore', () => ({
  useDesignStore: vi.fn((selector: (s: unknown) => unknown) => selector(mocks.designState)),
  NODE_SCALES: [{ edgeLabelFont: 11 }, { edgeLabelFont: 12 }, { edgeLabelFont: 13 }],
  EDGE_WIDTHS: [1, 2, 3],
  LABEL_FONT_OFFSETS: [-4, -2, 0, 2, 4, 6, 8],
}));

vi.mock('../../../../components/designer/edges/smartEdgePath', () => ({
  getSmartEdgePath: (...args: unknown[]) => mocks.getSmartEdgePath(...(args as [])),
  edgeArrowPath: () => 'M 100,0 L 88,4 L 88,-4 Z',
  EDGE_ARROW_STROKE_FRAC: 0.25,
}));

vi.mock('../../../../components/designer/edges/EdgeReshapeHandles', () => ({
  EdgeReshapeHandles: () => null,
}));

vi.mock('../../../../components/designer/edges/edgeEditingContext', async () => {
  const { createContext } = await vi.importActual<typeof import('react')>('react');
  const ctx = createContext({ onInsertRequest: () => {}, canWrite: false });
  return { EdgeEditingContext: ctx, EdgeInsertContext: ctx };
});

vi.mock('../../../../lib/summarizeExpression', () => ({ summarizeExpression: () => '' }));

import { LabeledEdge } from '../../../../components/designer/edges/LabeledEdge';

const baseProps = {
  id: 'e1',
  source: 'n1',
  target: 'n2',
  sourceX: 0,
  sourceY: 0,
  targetX: 100,
  targetY: 0,
  sourcePosition: Position.Right,
  targetPosition: Position.Left,
  selected: false,
  data: { label: 'On Success' },
  style: {},
  markerEnd: undefined,
} as const;

/** Re-renders the edge the way the canvas does: same values, freshly built objects. */
function rerenderWithEquivalentProps(rerender: (ui: React.ReactElement) => void) {
  rerender(
    <svg>
      <LabeledEdge {...baseProps} data={{ label: 'On Success' }} style={{}} />
    </svg>,
  );
}

describe('LabeledEdge — memoization', () => {
  beforeEach(() => {
    mocks.getSmartEdgePath.mockClear();
    mocks.useStoreCalls = 0;
    mocks.designState.premiumCanvas = true;
  });

  it('does not re-render when data is rebuilt with the same entries', () => {
    // The canvas projection hands every edge a fresh data object on every pass, so this is
    // the case that decides whether the memo does anything at all.
    const { rerender } = render(
      <svg>
        <LabeledEdge {...baseProps} data={{ label: 'On Success' }} style={{}} />
      </svg>,
    );
    const afterFirst = mocks.useStoreCalls;

    rerenderWithEquivalentProps(rerender);
    rerenderWithEquivalentProps(rerender);

    expect(mocks.useStoreCalls).toBe(afterFirst);
    expect(mocks.getSmartEdgePath.mock.calls.length).toBe(1);
  });

  it('recomputes the path when an endpoint moves', () => {
    const { rerender } = render(
      <svg>
        <LabeledEdge {...baseProps} />
      </svg>,
    );

    rerender(
      <svg>
        <LabeledEdge {...baseProps} targetX={260} />
      </svg>,
    );

    expect(mocks.getSmartEdgePath.mock.calls.length).toBe(2);
  });

  it('re-renders when the edge data actually changes', () => {
    const { rerender } = render(
      <svg>
        <LabeledEdge {...baseProps} data={{ label: 'On Success' }} />
      </svg>,
    );
    const afterFirst = mocks.useStoreCalls;

    rerender(
      <svg>
        <LabeledEdge {...baseProps} data={{ label: 'On Failure' }} />
      </svg>,
    );

    expect(mocks.useStoreCalls).toBeGreaterThan(afterFirst);
    // Geometry is unchanged, so the path is not recomputed even though the edge re-rendered.
    expect(mocks.getSmartEdgePath.mock.calls.length).toBe(1);
  });

  it('re-renders when selection changes', () => {
    const { rerender } = render(
      <svg>
        <LabeledEdge {...baseProps} selected={false} />
      </svg>,
    );
    const afterFirst = mocks.useStoreCalls;

    rerender(
      <svg>
        <LabeledEdge {...baseProps} selected />
      </svg>,
    );

    expect(mocks.useStoreCalls).toBeGreaterThan(afterFirst);
  });
});
