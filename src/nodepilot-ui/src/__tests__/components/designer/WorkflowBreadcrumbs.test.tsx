import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { Node } from '@xyflow/react';
import { WorkflowBreadcrumbs } from '../../../components/designer/WorkflowBreadcrumbs';
import type { Workflow } from '../../../types/api';

function mkWorkflow(id: string, name: string): Workflow {
  return {
    id, name, description: null,
    definitionJson: '{}', version: 1, isEnabled: true,
    createdAt: '2026-01-01', updatedAt: '2026-01-01',
    createdBy: null, updatedBy: null,
  };
}

const CHILD = mkWorkflow('11111111-aaaa-bbbb-cccc-000000000001', 'Child workflow');

/** startWorkflow node referencing a child by name or id. */
function callNode(id: string, ref: string, activityType = 'startWorkflow'): Node {
  const key = activityType === 'forEach' ? 'childWorkflowNameOrId' : 'workflowNameOrId';
  return {
    id, type: 'activity', position: { x: 0, y: 0 },
    data: { label: id, activityType, config: { [key]: ref } },
  };
}

function renderBreadcrumbs(nodes: Node[]) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // Seed the cache so ref-resolution is deterministic without hitting the network.
  qc.setQueryData(['workflows'], [CHILD]);
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <WorkflowBreadcrumbs nodes={nodes} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('WorkflowBreadcrumbs', () => {
  it('renders nothing when the workflow has no sub-workflow references', () => {
    const { container } = renderBreadcrumbs([
      { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { activityType: 'log', config: {} } },
    ]);
    expect(screen.queryByText(/Calls/i)).toBeNull();
    expect(container).toBeEmptyDOMElement();
  });

  it('renders a resolved reference as a link to the child workflow editor', () => {
    renderBreadcrumbs([callNode('n1', CHILD.name)]);
    const link = screen.getByRole('link', { name: /Child workflow/ });
    expect(link).toHaveAttribute('href', `/workflows/${CHILD.id}`);
  });

  it('resolves a forEach child reference too', () => {
    renderBreadcrumbs([callNode('n1', CHILD.id, 'forEach')]);
    expect(screen.getByRole('link', { name: /Child workflow/ })).toHaveAttribute(
      'href', `/workflows/${CHILD.id}`,
    );
  });

  it('renders an unresolved reference as a non-link broken-reference pill', () => {
    renderBreadcrumbs([callNode('n1', 'Does not exist')]);
    expect(screen.queryByRole('link')).toBeNull();
    // Broken-ref pill shows the raw ref text and carries the notFound tooltip.
    const pill = screen.getByText('Does not exist');
    expect(pill).toHaveAttribute('title', expect.stringContaining('Does not exist'));
  });

  it('skips dynamic references containing template placeholders', () => {
    const { container } = renderBreadcrumbs([callNode('n1', '{{globals.Target}}')]);
    expect(container).toBeEmptyDOMElement();
  });
});
