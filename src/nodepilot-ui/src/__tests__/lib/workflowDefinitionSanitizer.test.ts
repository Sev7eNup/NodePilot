import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { stripRuntimeDefinition } from '../../lib/workflowDefinitionSanitizer';

describe('stripRuntimeDefinition', () => {
  it('removes top-level runtime annotations from nodes and edges', () => {
    const nodes: Node[] = [
      {
        id: 'a',
        type: 'activity',
        position: { x: 0, y: 0 },
        data: {
          label: 'A',
          config: { script: 'Write-Host "__keep nested strings"' },
          __stats: { p95DurationMs: 100 },
          __criticalPath: { isCritical: true },
        },
      },
    ];
    const edges: Edge[] = [
      {
        id: 'e1',
        source: 'a',
        target: 'b',
        data: { label: 'ok', __varFlowHighlighted: true },
      },
    ];

    const clean = stripRuntimeDefinition({ nodes, edges });

    expect(clean.nodes[0].data).toEqual({
      label: 'A',
      config: { script: 'Write-Host "__keep nested strings"' },
    });
    expect(clean.edges[0].data).toEqual({ label: 'ok' });
    expect(nodes[0].data.__stats).toBeDefined();
    expect(edges[0].data?.__varFlowHighlighted).toBe(true);
  });
});
