import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { WORKFLOW_SNIPPETS, insertSnippet } from '../../lib/workflowSnippets';

/**
 * Pins two contracts of the quick-start snippet catalog:
 *   1. Every edge in a snippet references node local ids that exist in that same snippet.
 *   2. insertSnippet generates collision-free ids, applies the cursor offset, deselects
 *      the existing graph, and selects only the new nodes and edges.
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
    // Look up by id, not by array index: catalog order is only a UI sort order and may change.
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

    // The first node of try-catch-script has dx:0/dy:0, so it lands exactly on the origin.
    const tryNode = result.nodes.find((n) => (n.data as { label: string }).label === 'Try script');
    expect(tryNode?.position).toEqual({ x: 1000, y: 500 });

    // The catch node has dx:260/dy:150, so it lands at the origin plus that offset.
    const catchNode = result.nodes.find((n) => (n.data as { label: string }).label === 'On failure — log');
    expect(catchNode?.position).toEqual({ x: 1260, y: 650 });
  });

  it('rewritesEdgeSourceAndTarget_toFreshNodeIds', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const ids = new Set(result.nodes.map((n) => n.id));
    for (const edge of result.edges) {
      expect(ids.has(edge.source)).toBe(true);
      expect(ids.has(edge.target)).toBe(true);
      // Snippet-local ids such as 'try' or 'catch' must not survive as edge endpoints.
      expect(['try', 'catch', 'continue']).not.toContain(edge.source);
    }
  });

  it('newNodeIdsArrayMatchesInsertedNodes', () => {
    const result = insertSnippet(snippet(), { x: 0, y: 0 }, [], []);

    const insertedIds = result.nodes.filter((n) => n.selected).map((n) => n.id);
    expect(result.newNodeIds.sort()).toEqual(insertedIds.sort());
  });

  it('twoInsertsProduceDistinctIds', () => {
    // The id suffix comes from crypto.randomUUID, mocked above as a counter, so repeated
    // inserts of the same snippet must not clash.
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
    // The parallel-fanout snippet declares an outputVariable on its branch nodes.
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

    // An edge without an explicit condition gets an empty string, not undefined, so the
    // engine's schema validation accepts the JSON. It also carries an empty label, which is
    // how an edge that always runs is shown.
    const alwaysEdge = result.edges.find((e) => (e.data as { label: string }).label === '');
    expect(alwaysEdge).toBeDefined();
    expect((alwaysEdge!.data as { condition: string }).condition).toBe('');
  });
});
