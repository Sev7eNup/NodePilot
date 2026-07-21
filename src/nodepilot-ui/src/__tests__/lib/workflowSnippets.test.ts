import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { WORKFLOW_SNIPPETS, insertSnippet } from '../../lib/workflowSnippets';

/**
 * Snippets are append-only quick-start templates. We pin two contracts:
 *   1. The catalog itself stays internally consistent — every edge.from/to references a
 *      node that exists in the snippet.
 *   2. insertSnippet generates collision-free IDs, applies the cursor offset, deselects
 *      the existing graph, and selects only the new nodes/edges.
 */

beforeEach(() => {
  // Deterministic UUIDs make the suffix-asserted IDs stable across runs.
  let counter = 0;
  vi.spyOn(globalThis.crypto, 'randomUUID').mockImplementation(() => {
    counter += 1;
    // insertSnippet calls .slice(0, 6) so the suffix must vary in the first 6 chars.
    const suffix = String(counter).padStart(6, 'a');
    return `${suffix}-0000-0000-0000-000000000000` as `${string}-${string}-${string}-${string}-${string}`;
  });
});

describe('WORKFLOW_SNIPPETS catalog', () => {
  it('hasFourSnippets', () => {
    expect(WORKFLOW_SNIPPETS).toHaveLength(4);
  });

  it('everyEdgeReferencesAnExistingLocalId', () => {
    for (const snippet of WORKFLOW_SNIPPETS) {
      const localIds = new Set(snippet.nodes.map((n) => n.localId));
      for (const e of snippet.edges) {
        expect(localIds.has(e.fromLocalId)).toBe(true);
        expect(localIds.has(e.toLocalId)).toBe(true);
      }
    }
  });

  it('snippetIdsAreUnique', () => {
    const ids = WORKFLOW_SNIPPETS.map((s) => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('everyNodeHasActivityType', () => {
    for (const snippet of WORKFLOW_SNIPPETS) {
      for (const node of snippet.nodes) {
        expect(node.activityType).toBeTruthy();
        expect(typeof node.activityType).toBe('string');
      }
    }
  });
});

describe('insertSnippet', () => {
  function snippet() {
    // Look up by id, not by array index — the catalog order is just UI sort order
    // and can change without breaking these tests.
    const found = WORKFLOW_SNIPPETS.find((s) => s.id === 'try-catch-script');
    if (!found) throw new Error("Test fixture missing snippet 'try-catch-script'.");
    return found;
  }

  it('returnsNewArraysWithBothExistingAndPasted', () => {
    const existingNode: Node = {
      id: 'old-1', type: 'activity', position: { x: 0, y: 0 },
      data: { label: 'Pre' }, selected: true,
    };
    const result = insertSnippet(snippet(), { x: 100, y: 200 }, [existingNode], []);

    expect(result.nodes).toHaveLength(1 + snippet().nodes.length);
    expect(result.edges).toHaveLength(snippet().edges.length);
  });

  it('deselectsAllPreExistingNodesAndEdges', () => {
    const existingNode: Node = {
      id: 'old-1', type: 'activity', position: { x: 0, y: 0 },
      data: { label: 'Pre' }, selected: true,
    };
    const existingEdge: Edge = {
      id: 'old-edge', source: 'old-1', target: 'old-1', selected: true,
    };
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [existingNode], [existingEdge]);

    const oldNode = result.nodes.find((n) => n.id === 'old-1');
    const oldEdge = result.edges.find((e) => e.id === 'old-edge');
    expect(oldNode?.selected).toBe(false);
    expect(oldEdge?.selected).toBe(false);
  });

  it('selectsAllInsertedNodesAndEdges', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    expect(result.nodes.every((n) => n.selected === true)).toBe(true);
    expect(result.edges.every((e) => e.selected === true)).toBe(true);
  });

  it('appliesOriginOffsetToEachNode', () => {
    const result = insertSnippet(snippet(), { x: 1000, y: 500 }, [], []);

    // try-catch-script first node has dx:0/dy:0 → at exactly (1000, 500)
    const tryNode = result.nodes.find((n) => (n.data as { label: string }).label === 'Try script');
    expect(tryNode?.position).toEqual({ x: 1000, y: 500 });

    // catch node: dx:260, dy:150 → (1260, 650)
    const catchNode = result.nodes.find((n) => (n.data as { label: string }).label === 'On failure — log');
    expect(catchNode?.position).toEqual({ x: 1260, y: 650 });
  });

  it('rewritesEdgeSourceAndTarget_toFreshNodeIds', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const ids = new Set(result.nodes.map((n) => n.id));
    for (const edge of result.edges) {
      expect(ids.has(edge.source)).toBe(true);
      expect(ids.has(edge.target)).toBe(true);
      // Old localIds (e.g. 'try', 'catch') must not leak into edge endpoints unmodified.
      expect(['try', 'catch', 'continue']).not.toContain(edge.source);
    }
  });

  it('newNodeIdsArrayMatchesInsertedNodes', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const insertedIds = result.nodes.filter((n) => n.selected).map((n) => n.id);
    expect(result.newNodeIds.sort()).toEqual(insertedIds.sort());
  });

  it('twoInsertsProduceDistinctIds', () => {
    // The id-suffix uses crypto.randomUUID, mocked above as a counter so each call yields
    // a different suffix. This pins the no-clash contract.
    const a = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);
    const b = insertSnippet(snippet(), { x: 0, y: 0 }, a.nodes, a.edges);

    const allIds = b.nodes.map((n) => n.id);
    expect(new Set(allIds).size).toBe(allIds.length);
  });

  it('nodeDataPreservesActivityTypeAndConfig', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const tryNode = result.nodes.find((n) => (n.data as { label: string }).label === 'Try script');
    const data = tryNode!.data as Record<string, unknown>;
    expect(data.activityType).toBe('runScript');
    expect(data.config).toBeDefined();
    expect((data.config as Record<string, unknown>).engine).toBe('auto');
  });

  it('nodeDataIncludesOutputVariableWhenDeclared', () => {
    // parallel-fanout snippet has branch nodes with outputVariable set
    const fanout = WORKFLOW_SNIPPETS.find((s) => s.id === 'parallel-fanout')!;
    const result = insertSnippet(fanout, { x: 0, y: 0 }, [], []);

    const branchA = result.nodes.find((n) => (n.data as { label: string }).label === 'Branch A');
    expect((branchA!.data as Record<string, unknown>).outputVariable).toBe('a');
  });

  it('edgeDataIncludesLabelAndConditionDefaults', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const onSuccess = result.edges.find((e) => (e.data as { label: string }).label === 'On Success');
    expect(onSuccess).toBeDefined();
    expect((onSuccess!.data as { condition: string }).condition).toBe('try.success');

    // Edges without explicit condition get an empty string (not undefined) so the engine's
    // schema validation doesn't reject the JSON.
    const alwaysEdge = result.edges.find((e) => (e.data as { label: string }).label === 'Always');
    expect((alwaysEdge!.data as { condition: string }).condition).toBe('');
  });
});
