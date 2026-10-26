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
    // The traversal walks objects and arrays recursively; the dotted path key of a hit is part
    // of the contract and is asserted here.
    const nodes = [
      node('n1', 'Run Script', {
        script: 'Get-Content C:\\logs\\app.log',
        headers: [{ key: 'X-Trace', value: 'log-correlation' }],
      }),
    ];

    const hits = findMatches('log', nodes, [], { nodeLabels: false, edgeLabels: false, configValues: true });

    // Matches script and headers[0].value, which both contain "log".
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

    // Only nodeLabels is enabled, so there is a single match: the node label.
    const hits = findMatches('My', nodes, edges, { nodeLabels: true, edgeLabels: false, configValues: false });
    expect(hits).toHaveLength(1);
    expect(hits[0].kind).toBe('node-label');
  });

  it('returnsEveryMatch_beyondTheDisplayLimit', () => {
    // The overlay lists at most MAX_DISPLAYED_MATCHES rows, but the match set that drives
    // Replace All must not be shortened.
    const manyNodes = Array.from({ length: 100 }, (_, i) => node(`n${i}`, 'Match'));
    const hits = findMatches('Match', manyNodes, [], ALL_SCOPES);
    expect(hits).toHaveLength(100);
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

  it('replacesEveryNode_whenMatchesExceedTheDisplayLimit', () => {
    const nodes = Array.from({ length: 51 }, (_, i) => node(`n${i}`, 'x', { url: 'https://old.example/api' }));
    const matches = findMatches('old.example', nodes, [], { nodeLabels: false, edgeLabels: false, configValues: true });

    const { nodes: out } = applyReplaceAll(matches, 'old.example', 'new.example', nodes, []);

    const urls = out.map((n) => ((n.data as Record<string, unknown>).config as { url: string }).url);
    expect(urls).toHaveLength(51);
    expect(urls.every((u) => u === 'https://new.example/api')).toBe(true);
  });

  it('replacesAllOccurrences_notJustFirst', () => {
    // The replacement uses replaceAll, so a label with several occurrences is fully rewritten.
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

  it('replacingOneConfigMatch_leavesOtherFieldsOfTheSameNodeUnchanged', () => {
    const nodes = [node('n1', 'Deploy prod', { workingDirectory: '/srv/prod/app', script:'Restart-Service prod-api' })];
    const selected = findMatches('prod', nodes, [], ALL_SCOPES)
      .filter((m) => m.configKeyPath === 'workingDirectory');

    const { nodes: out } = applyReplaceAll(selected, 'prod', 'test', nodes, []);

    const data = out[0].data as { label: string; config: Record<string, string> };
    expect(data.config.workingDirectory).toBe('/srv/test/app');
    expect(data.config.script).toBe('Restart-Service prod-api');
    expect(data.label).toBe('Deploy prod');
  });

  it('replacingALabelMatch_leavesConfigUnchanged', () => {
    const nodes = [node('n1', 'Old step', { script: 'Old-Command' })];
    const labelOnly = findMatches('Old', nodes, [], ALL_SCOPES).filter((m) => m.kind === 'node-label');

    const { nodes: out } = applyReplaceAll(labelOnly, 'Old', 'New', nodes, []);

    const data = out[0].data as { label: string; config: Record<string, string> };
    expect(data.label).toBe('New step');
    expect(data.config.script).toBe('Old-Command');
  });

  it('replacesAtAConfigKeyThatContainsADot', () => {
    const nodes = [node('n1', 'Call', { headers: { 'X.Old': 'Old-value', X: { Old: 'Old-nested' } } })];
    const selected = findMatches('Old', nodes, [], ALL_SCOPES)
      .filter((m) => m.configPath?.join('/') === 'headers/X.Old');

    const { nodes: out } = applyReplaceAll(selected, 'Old', 'New', nodes, []);

    const cfg = (out[0].data as { config: { headers: Record<string, unknown> } }).config;
    expect(cfg.headers['X.Old']).toBe('New-value');
    expect(cfg.headers.X).toEqual({ Old: 'Old-nested' });
  });

  it('doesNotMutateOriginalArrays', () => {
    // applyReplaceAll must produce new arrays instead of splicing the inputs in place.
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
