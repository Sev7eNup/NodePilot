import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Position } from '@xyflow/react';

/**
 * LabeledEdge — disabled-edge visual indicator tests.
 *
 * Pinned behaviors:
 *   - disabled=true  → ⊘ badge + "disabled" text rendered
 *   - disabled=false → no badge
 *   - disabled absent → no badge
 *   - disabled edge label gets muted style, not condition-color style
 */

const mocks = vi.hoisted(() => ({
  designState: {
    nodeScaleIndex: 1,
    labelFontOffsetIndex: 2,
    edgeWidthIndex: 1,
    edgeRouting: 'smart',
    edgesAnimated: false,
    premiumCanvas: false,
  },
  smartSegments: [['M0,0 C50,0 50,0 100,0', 50, 0]] as unknown[],
}));

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual<typeof import('@xyflow/react')>('@xyflow/react');
  return {
    ...actual,
    BaseEdge: ({ path, style, markerEnd }: { path: string; style?: React.CSSProperties; markerEnd?: string }) => (
      <path data-testid="base-edge" d={path} data-marker-end={markerEnd ?? ''} style={style} />
    ),
    EdgeLabelRenderer: ({ children }: { children: React.ReactNode }) => (
      <div data-testid="edge-label-renderer">{children}</div>
    ),
    useStore: vi.fn((selector: (s: unknown) => unknown) =>
      selector({ nodeLookup: new Map() }),
    ),
  };
});

vi.mock('../../../../stores/designStore', () => ({
  useDesignStore: vi.fn((selector: (s: unknown) => unknown) =>
    selector(mocks.designState),
  ),
  NODE_SCALES: [
    { edgeLabelFont: 11 },
    { edgeLabelFont: 12 },
    { edgeLabelFont: 13 },
  ],
  EDGE_WIDTHS: [1, 2, 3],
  LABEL_FONT_OFFSETS: [-4, -2, 0, 2, 4, 6, 8],
}));

vi.mock('../../../../components/designer/edges/smartEdgePath', () => ({
  getSmartEdgePath: () => mocks.smartSegments,
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

vi.mock('../../../../lib/summarizeExpression', () => ({
  summarizeExpression: () => '',
}));

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
  data: {},
  style: {},
  markerEnd: undefined,
} as const;

describe('LabeledEdge — disabled indicator', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.designState.premiumCanvas = false;
    mocks.designState.edgesAnimated = false;
    mocks.designState.labelFontOffsetIndex = 2;
    mocks.smartSegments = [['M0,0 C50,0 50,0 100,0', 50, 0]] as unknown[];
  });

  it('renders ban badge and "disabled" text when data.disabled=true', () => {
    const { container } = render(
      <svg>
        <LabeledEdge {...baseProps} data={{ disabled: true }} />
      </svg>,
    );
    expect(screen.getByText('disabled')).toBeInTheDocument();
    // Carbon MisuseOutline icon (size 9) marks the disabled edge.
    expect(container.querySelector('svg[width="9"]')).toBeInTheDocument();
  });

  it('does NOT render disabled badge when data.disabled=false', () => {
    render(
      <svg>
        <LabeledEdge {...baseProps} data={{ disabled: false }} />
      </svg>,
    );
    expect(screen.queryByText('disabled')).not.toBeInTheDocument();
  });

  it('does NOT render disabled badge when disabled field is absent', () => {
    render(
      <svg>
        <LabeledEdge {...baseProps} data={{}} />
      </svg>,
    );
    expect(screen.queryByText('disabled')).not.toBeInTheDocument();
  });

  it('applies muted label style when disabled=true and a label is set', () => {
    render(
      <svg>
        <LabeledEdge
          {...baseProps}
          data={{ disabled: true, label: 'On Success', condition: 'n1.success' }}
        />
      </svg>,
    );
    const labelEl = screen.getByText('On Success');
    // classList.contains works for both HTML and SVG elements
    expect(labelEl.classList.contains('text-outline')).toBe(true);
    expect(labelEl.classList.contains('text-green-700')).toBe(false);
  });

  it('scales edge label font with the designer label offset', () => {
    mocks.designState.labelFontOffsetIndex = 4; // +4px node-label offset -> +3px edge-label bump
    render(
      <svg>
        <LabeledEdge {...baseProps} data={{ label: 'Archive=TRUE' }} />
      </svg>,
    );

    expect(screen.getByText('Archive=TRUE')).toHaveStyle({ fontSize: '15px' });
  });

  it('renders premium arrow polygon from smartEdgePath instead of browser markerEnd', () => {
    mocks.designState.premiumCanvas = true;
    mocks.smartSegments = [[
      'M0,0 C40,0 70,0 88,0',
      50,
      0,
      { tipX: 100, tipY: 0, baseX: 88, baseY: 0, angle: 0, width: 9 },
    ]] as unknown[];

    render(
      <svg>
        <LabeledEdge {...baseProps} data={{ condition: 'SyncPac=TRUE' }} />
      </svg>,
    );

    expect(screen.getByTestId('base-edge')).toHaveAttribute('data-marker-end', '');
    expect(screen.getByTestId('premium-edge-arrow-e1')).toHaveAttribute('d', 'M 100,0 L 88,4 L 88,-4 Z');
  });
});
