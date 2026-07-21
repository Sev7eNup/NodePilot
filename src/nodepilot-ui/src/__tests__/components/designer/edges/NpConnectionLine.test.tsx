import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { Position, ConnectionLineType } from '@xyflow/react';
import { NpConnectionLine } from '../../../../components/designer/edges/NpConnectionLine';
import type { ConnectionLineComponentProps } from '@xyflow/react';

const baseProps = {
  fromX: 0,
  fromY: 0,
  toX: 120,
  toY: 40,
  fromPosition: Position.Right,
  toPosition: Position.Left,
  connectionLineType: ConnectionLineType.Bezier,
  fromNode: undefined,
  fromHandle: undefined,
  connectionStatus: null,
} as unknown as ConnectionLineComponentProps;

describe('NpConnectionLine', () => {
  it('renders a primary-tokened dashed path with a docking dot at the cursor', () => {
    const { container } = render(
      <svg>
        <NpConnectionLine {...baseProps} />
      </svg>,
    );
    const path = container.querySelector('path.np-connection-dash');
    expect(path).toBeInTheDocument();
    expect(path?.getAttribute('stroke')).toBe('var(--color-primary)');
    expect(path?.getAttribute('stroke-dasharray')).toBe('6 4');

    const dot = container.querySelector('circle');
    expect(dot).toBeInTheDocument();
    expect(dot?.getAttribute('cx')).toBe('120');
    expect(dot?.getAttribute('cy')).toBe('40');
    expect(dot?.getAttribute('stroke')).toBe('var(--color-primary)');
  });
});
