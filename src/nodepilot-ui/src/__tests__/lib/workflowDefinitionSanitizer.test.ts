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

  it('removes canvas projection props that carry no prefix', () => {
    // useDisplayedGraph writes these; they are render state, not part of the definition.
    // Undo used to write the projected graph back, so saved definitions can already carry them.
    const nodes: Node[] = [
      {
        id: 'a',
        type: 'activity',
        position: { x: 0, y: 0 },
        hidden: true,
        className: 'np-dock-target',
        data: { label: 'A', inDegreeCount: 2 },
      },
    ];
    const edges: Edge[] = [
      { id: 'e1', source: 'a', target: 'b', hidden: true, animated: true, data: { label: 'ok' } },
    ];

    const clean = stripRuntimeDefinition({ nodes, edges });

    expect(clean.nodes[0].data).toEqual({ label: 'A' });
    expect(clean.nodes[0]).not.toHaveProperty('hidden');
    expect(clean.nodes[0]).not.toHaveProperty('className');
    expect(clean.edges[0]).not.toHaveProperty('hidden');
    expect(clean.edges[0]).not.toHaveProperty('animated');
    // The input is never mutated.
    expect(nodes[0].hidden).toBe(true);
    expect(edges[0].animated).toBe(true);
  });

  it('keeps object identity when there is nothing to strip', () => {
    // persistableDefinition runs this on every graph change; an unchanged graph must not
    // produce new objects.
    const nodes: Node[] = [{ id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A' } }];
    const edges: Edge[] = [{ id: 'e1', source: 'a', target: 'b', data: { label: 'ok' } }];

    const clean = stripRuntimeDefinition({ nodes, edges });

    expect(clean.nodes[0]).toBe(nodes[0]);
    expect(clean.edges[0]).toBe(edges[0]);
  });
});
