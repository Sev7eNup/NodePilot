import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { findMatches, applyReplaceAll, type FindReplaceScopes } from '../../lib/findReplace';

const ALL_SCOPES: FindReplaceScopes = { nodeLabels: true, edgeLabels: true, configValues: true };

function node(id: string, label: string, config: Record<string, unknown> = {}): Node {
  return {
    id, type: 'activity', position: { x: 0, y: 0 },
    data: { label, activityType: 'runScript', config },
  };
}

function edge(id: string, label: string): Edge {
  return { id, source: 'a', target: 'b', type: 'labeled', data: { label } };
}

describe('findMatches', () => {
  it('emptyQuery_returnsEmptyArray', () => {
    const nodes = [node('n1', 'My Node')];
    expect(findMatches('', nodes, [], ALL_SCOPES)).toEqual([]);
  });

  it('matchesNodeLabels_caseInsensitive', () => {
    const nodes = [node('n1', 'CHECK Disk'), node('n2', 'Send email')];

    const hits = findMatches('check', nodes, [], { ...ALL_SCOPES, edgeLabels: false, configValues: false });

    expect(hits).toHaveLength(1);
    expect(hits[0].kind).toBe('node-label');
    expect(hits[0].nodeId).toBe('n1');
  });

  it('matchesEdgeLabels_caseInsensitive', () => {
    const edges = [edge('e1', 'On Success'), edge('e2', 'On Failure')];

    const hits = findMatches('FAILURE', [], edges, ALL_SCOPES);

    expect(hits).toHaveLength(1);
    expect(hits[0].kind).toBe('edge-label');
    expect(hits[0].edgeId).toBe('e2');
  });

  it('matchesConfigValues_traversesNestedObjectsAndArrays', () => {
    // The traversal walks objects + arrays recursively. We pin the path-rendering
    // shape so a refactor that breaks the dotted path key doesn't go unnoticed.
    const nodes = [
      node('n1', 'Run Script', {
        script: 'Get-Content C:\\logs\\app.log',
        headers: [{ key: 'X-Trace', value: 'log-correlation' }],
      }),
    ];

    const hits = findMatches('log', nodes, [], { nodeLabels: false, edgeLabels: false, configValues: true });

    // Should match script + headers[0].value (both contain "log")
    expect(hits.length).toBeGreaterThanOrEqual(2);
    expect(hits.some((h) => h.configKeyPath === 'script')).toBe(true);
    expect(hits.some((h) => h.configKeyPath === 'headers.0.value')).toBe(true);
    hits.forEach((h) => expect(h.kind).toBe('config'));
  });

  it('snippetSurroundsMatchWith20CharsContext', () => {
    // Matches return a contextSnippet with up to 20 chars on each side and an ellipsis
    // marker when the actual text is longer. Keeps the find-results UI compact.
    const nodes = [node('n1', 'Run Script', {
      script: 'aaaaaaaaaaaaaaaaaaaaaa NEEDLE bbbbbbbbbbbbbbbbbbbbbbbb',
    })];

    const hits = findMatches('NEEDLE', nodes, [], { nodeLabels: false, edgeLabels: false, configValues: true });

    expect(hits).toHaveLength(1);
    expect(hits[0].contextSnippet).toContain('NEEDLE');
    // Has ellipsis on at least one side because the surrounding text exceeds 20 chars.
    expect(hits[0].contextSnippet).toMatch(/^…|…$/);
  });

  it('respectsScopes_doesNotMatchOutsideEnabledScopes', () => {
    const nodes = [node('n1', 'My Node', { script: 'My script content' })];
    const edges = [edge('e1', 'My edge label')];

    // Only nodeLabels enabled → 1 match (the node label).
    const hits = findMatches('My', nodes, edges, { nodeLabels: true, edgeLabels: false, configValues: false });
    expect(hits).toHaveLength(1);
    expect(hits[0].kind).toBe('node-label');
  });

  it('cappedAt50Hits', () => {
    // Hard cap so a search like "the" doesn't return thousands of rows and freeze the UI.
    const manyNodes = Array.from({ length: 100 }, (_, i) => node(`n${i}`, 'Match'));
    const hits = findMatches('Match', manyNodes, [], ALL_SCOPES);
    expect(hits).toHaveLength(50);
  });
});

describe('applyReplaceAll', () => {
  it('replacesNodeLabel_inMatchedNodes', () => {
    const nodes = [node('n1', 'OldText'), node('n2', 'OldText')];
    const matches = findMatches('Old', nodes, [], { nodeLabels: true, edgeLabels: false, configValues: false });

    const { nodes: out } = applyReplaceAll(matches, 'Old', 'New', nodes, []);

    expect((out[0].data as Record<string, unknown>).label).toBe('NewText');
    expect((out[1].data as Record<string, unknown>).label).toBe('NewText');
  });

  it('replacesAllOccurrences_notJustFirst', () => {
    // The replacement uses replaceAll, not replace — confirm a multi-occurrence label
    // gets fully rewritten.
    const nodes = [node('n1', 'cat dog cat dog cat')];
    const matches = findMatches('cat', nodes, [], { nodeLabels: true, edgeLabels: false, configValues: false });

    const { nodes: out } = applyReplaceAll(matches, 'cat', 'fox', nodes, []);

    expect((out[0].data as Record<string, unknown>).label).toBe('fox dog fox dog fox');
  });

  it('replacesNestedConfigValues', () => {
    const nodes = [node('n1', 'My Node', { headers: [{ key: 'X-Old', value: 'still-Old' }] })];
    const matches = findMatches('Old', nodes, [], { nodeLabels: false, edgeLabels: false, configValues: true });

    const { nodes: out } = applyReplaceAll(matches, 'Old', 'New', nodes, []);

    const cfg = (out[0].data as Record<string, unknown>).config as { headers: Array<{ key: string; value: string }> };
    expect(cfg.headers[0].key).toBe('X-New');
    expect(cfg.headers[0].value).toBe('still-New');
  });

  it('doesNotMutateOriginalArrays', () => {
    // Immutability check — applyReplaceAll must produce new arrays, not splice in place.
    const nodes = [node('n1', 'OldText')];
    const original = JSON.parse(JSON.stringify(nodes));
    const matches = findMatches('Old', nodes, [], ALL_SCOPES);

    applyReplaceAll(matches, 'Old', 'New', nodes, []);

    expect(nodes).toEqual(original);
  });

  it('replacesEdgeLabel', () => {
    const edges = [edge('e1', 'On OldStatus')];
    const matches = findMatches('Old', [], edges, { nodeLabels: false, edgeLabels: true, configValues: false });

    const { edges: out } = applyReplaceAll(matches, 'Old', 'New', [], edges);

    expect((out[0].data as Record<string, unknown>).label).toBe('On NewStatus');
  });
});
