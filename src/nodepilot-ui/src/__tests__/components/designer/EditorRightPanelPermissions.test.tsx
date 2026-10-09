import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { EditorRightPanel } from '../../../components/designer/EditorRightPanel';
import { useDesignStore } from '../../../stores/designStore';

vi.mock('../../../components/designer/PropertiesPanel', () => ({ PropertiesPanel: () => null }));

describe('EditorRightPanel bulk permissions', () => {
  it.each([false, true])('respects canWrite=%s for a real multiselection', (canWrite) => {
    useDesignStore.setState({ designerMode: 'expert' });
    const apply = vi.fn();
    render(<EditorRightPanel fullscreen={false}
      nodes={['a', 'b'].map(id => ({ id, type: 'activity', selected: true, position: { x: 0, y: 0 }, data: {} }))}
      edges={[]} selectedNode={null} selectedEdge={null} machines={[]} credentials={[]}
      workflowId="workflow" canWrite={canWrite} panelSize={320}
      panelHandleProps={{ onMouseDown: vi.fn(), onDoubleClick: vi.fn() }} setNodes={vi.fn()}
      setSelected={vi.fn()} setLeftTab={vi.fn()} setLeftCollapsed={vi.fn()} handleBulkApply={apply}
      handleNodeDataUpdate={vi.fn()} handleEdgeUpdate={vi.fn()} handleEdgeDelete={vi.fn()} onVarHover={vi.fn()} />);

    const field = screen.getAllByRole('combobox')[1];
    fireEvent.change(field, { target: { value: 'true' } });
    const button = screen.getByRole('button', { name: /Apply state/i });
    fireEvent.click(button);
    if (canWrite) expect(apply).toHaveBeenCalledWith(['a', 'b'], { disabled: true }, undefined);
    else {
      expect(field).toBeDisabled();
      expect(button).toBeDisabled();
      expect(apply).not.toHaveBeenCalled();
    }
  });
});
