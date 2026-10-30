import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Edge, Node } from '@xyflow/react';
import {
  parseFragment,
  remapConditionStep,
  remapFragment,
  SECRET_CONFIG_KEYS,
  selectFragment,
  serializeFragment,
  type WorkflowFragment,
} from '../../lib/workflowFragment';

/**
 * Workflow fragments carry nodes between the canvas, the in-memory clipboard and JSON text.
 * Pinned here: which edges travel with a selection, what the JSON text leaves out, how pasted
 * text is checked, and that every reference to an old node ID follows it to the new one.
 */

function node(id: string, extra: Partial<Node> = {}): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label: id, activityType: 'log' }, ...extra };
}

function edge(id: string, source: string, target: string, data: Record<string, unknown> = {}): Edge {
  return { id, source, target, type: 'labeled', data };
}

const NO_GROUPS: ReadonlySet<string> = new Set();

describe('selectFragment', () => {
  it('keepsOnlyEdgesBetweenSelectedNodes', () => {
    const fragment = selectFragment(
      [node('a'), node('b'), node('c')],
      [edge('ab', 'a', 'b'), edge('bc', 'b', 'c')],
      ['a', 'b'],
    );

    expect(fragment.nodes.map((n) => n.id)).toEqual(['a', 'b']);
    expect(fragment.edges.map((e) => e.id)).toEqual(['ab']);
  });

  it('copiesNodeData_soLaterCanvasEditsDoNotLeakIn', () => {
    const original = node('a');
    const fragment = selectFragment([original], [], ['a']);

    (original.data as Record<string, unknown>).label = 'changed';

    expect(fragment.nodes[0].data.label).toBe('a');
  });
});

describe('serializeFragment', () => {
  it.each([
    ['Authorization: Bearer literal-token', '***'],
    ['Accept: application/json\r\nX-Custom-Auth: x', '***'],
    ['X-Custom-Auth: x\r\nAccept: application/json', '***'],
    ['Authorization: Bearer {{globals.Token}}', 'Authorization: Bearer {{globals.Token}}'],
    ['X-Custom-Auth: {{globals.Token}}-literal', '***'],
    ['Accept: application/json\nContent-Type: text/plain', 'Accept: application/json\nContent-Type: text/plain'],
  ])('headerString_%s_isRedactedLikeExport', (headers, expected) => {
    const fragment = { nodes: [node('a', { data: { activityType: 'restApi', config: { headers } } })], edges: [] };
    expect(JSON.parse(serializeFragment(fragment)).nodes[0].data.config.headers).toBe(expected);
    expect(fragment.nodes[0].data.config).toEqual({ headers });
  });

  it('headerObjects_preserveReferencesAndMaskCustomLiteralCredentials', () => {
    const headers = { Authorization: 'Bearer {{globals.Token}}', 'X-Custom-Auth': 'x', Accept: 'application/json' };
    const fragment = { nodes: [node('a', { data: { config: { headers } } })], edges: [] };
    expect(JSON.parse(serializeFragment(fragment)).nodes[0].data.config.headers).toEqual({
      Authorization: 'Bearer {{globals.Token}}', 'X-Custom-Auth': '***', Accept: 'application/json',
    });
  });

  it.each([
    'sk_' + 'test_' + 'a'.repeat(16),
    'AKIA' + 'A'.repeat(16),
    'ghp_' + 'a'.repeat(20),
    'glpat-' + 'a'.repeat(20),
    'xoxb-' + 'a'.repeat(10),
    'eyJ' + 'a'.repeat(10) + '.' + 'b'.repeat(10) + '.' + 'c'.repeat(10),
    '-----BEGIN RSA PRIVATE KEY-----',
  ])('providerTokenInFreeText_isMasked (%s)', (token) => {
    const fragment = { nodes: [node('a', { data: { config: { body: `prefix ${token} suffix` } } })], edges: [] };
    expect(JSON.parse(serializeFragment(fragment)).nodes[0].data.config.body).toBe('***');
  });

  it('backendHeaderPolicy_isRespectedForEveryDeclaredHeader', () => {
    const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)),
      '../../../../../src/NodePilot.Core/WorkflowDefinitions/WorkflowSecretContent.cs'), 'utf8');
    for (const [field, expected] of [['PublicHttpHeaderNames', 'literal'], ['CredentialHeaderNames', '***']]) {
      const start = source.indexOf(`${field} =`);
      const names = [...source.slice(start, source.indexOf('};', start)).matchAll(/"([^"]+)"/g)].map((m) => m[1]);
      expect(names.length).toBeGreaterThan(0);
      for (const name of names) {
        const headers = { [name.toLowerCase()]: 'literal' };
        const fragment = { nodes: [node('a', { data: { config: { headers } } })], edges: [] };
        expect(JSON.parse(serializeFragment(fragment)).nodes[0].data.config.headers[name.toLowerCase()]).toBe(expected);
      }
    }
  });

  it.each([
    ['body', '{"password":"literal-secret"}', '***'],
    ['script', '$token = "literal-secret"', '***'],
    ['content', 'Authorization: Bearer literal-token', '***'],
    ['body', '{"password":"{{globals.Password}}"}', '{"password":"{{globals.Password}}"}'],
  ])('contentUnder_%s_isRedactedLikeExport', (key, value, expected) => {
    const fragment = { nodes: [node('a', { data: { config: { [key]: value } } })], edges: [] };
    expect(JSON.parse(serializeFragment(fragment)).nodes[0].data.config[key]).toBe(expected);
  });

  it('dropsCanvasStateAndRuntimeData', () => {
    const fragment: WorkflowFragment = {
      nodes: [node('a', {
        selected: true,
        dragging: false,
        measured: { width: 50, height: 50 },
        data: { label: 'A', activityType: 'log', __liveStatus: 'Running', inDegreeCount: 2 },
      })],
      edges: [{ ...edge('e', 'a', 'a'), selected: true, animated: true }],
    };

    const parsed = JSON.parse(serializeFragment(fragment));

    expect(parsed.nodes[0]).toEqual({
      id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A', activityType: 'log' },
    });
    expect(parsed.edges[0]).not.toHaveProperty('selected');
    expect(parsed.edges[0]).not.toHaveProperty('animated');
  });

  it('secretConfigValues_areMasked', () => {
    const fragment = selectFragment([node('a', {
      data: {
        label: 'A',
        activityType: 'restApi',
        config: {
          url: 'https://example.test',
          apiKey: 'k-123',
          Password: 'pw',
          connectionString: 'Server=x;Password=y',
          secret: 'hmac',
          timeoutSeconds: 30,
          headers: { Authorization: 'Bearer abc', Accept: 'text/plain' },
          nested: [{ token: 't-1', name: 'keep' }],
        },
      },
    })], [], ['a']);

    const config = JSON.parse(serializeFragment(fragment)).nodes[0].data.config;

    expect(config).toEqual({
      url: 'https://example.test',
      apiKey: '***',
      Password: '***',
      connectionString: '***',
      secret: '***',
      timeoutSeconds: 30,
      headers: { Authorization: '***', Accept: 'text/plain' },
      nested: [{ token: '***', name: 'keep' }],
    });
  });

  it('emptyAndNullSecretValues_stayAsTheyAre', () => {
    const fragment = selectFragment([node('a', {
      data: { label: 'A', activityType: 'restApi', config: { apiKey: '', password: null } },
    })], [], ['a']);

    const config = JSON.parse(serializeFragment(fragment)).nodes[0].data.config;

    expect(config).toEqual({ apiKey: '', password: null });
  });

  it('serializing_doesNotChangeTheFragment', () => {
    const fragment = selectFragment([node('a', {
      data: { label: 'A', activityType: 'restApi', config: { apiKey: 'k-123' } },
    })], [], ['a']);

    serializeFragment(fragment);

    expect((fragment.nodes[0].data as { config: { apiKey: string } }).config.apiKey).toBe('k-123');
  });

  it('maskedCopy_isFlaggedOnPaste', () => {
    const fragment = selectFragment([node('a', {
      data: { label: 'A', activityType: 'restApi', config: { apiKey: 'k-123', secret: 's', url: 'u' } },
    })], [], ['a']);

    const result = parseFragment(serializeFragment(fragment));

    expect(result.ok && result.redactedValues).toBe(2);
  });

  it('secretKeyList_matchesBackendSecretConfigKeys', () => {
    const csPath = join(
      dirname(fileURLToPath(import.meta.url)),
      '..', '..', '..', '..', '..', 'src', 'NodePilot.Core', 'WorkflowDefinitions', 'WorkflowSecretKeys.cs',
    );
    const source = readFileSync(csPath, 'utf8');
    const block = source.slice(source.indexOf('SecretConfigKeys ='), source.indexOf('};', source.indexOf('SecretConfigKeys =')));
    const backend = [...block.replace(/\/\/.*$/gm, '').matchAll(/"([^"]+)"/g)].map((m) => m[1].toLowerCase());

    expect([...SECRET_CONFIG_KEYS].sort()).toEqual([...backend].sort());
  });

  it('roundTripsThroughParseFragment', () => {
    const fragment = selectFragment([node('a'), node('b')], [edge('ab', 'a', 'b')], ['a', 'b']);

    const result = parseFragment(serializeFragment(fragment));

    expect(result.ok).toBe(true);
    if (result.ok) expect(result.fragment.nodes.map((n) => n.id)).toEqual(['a', 'b']);
  });
});

describe('parseFragment', () => {
  it('invalidJson_returnsError', () => {
    expect(parseFragment('{ nodes: ')).toEqual({ ok: false, error: 'invalidJson' });
  });

  it('missingOrEmptyNodes_returnsError', () => {
    expect(parseFragment('{}')).toEqual({ ok: false, error: 'noNodes' });
    expect(parseFragment('{"nodes": []}')).toEqual({ ok: false, error: 'noNodes' });
    expect(parseFragment('[1, 2]')).toEqual({ ok: false, error: 'noNodes' });
  });

  it('unknownNodeType_returnsError', () => {
    const text = JSON.stringify({ nodes: [{ id: 'a', type: 'script', position: { x: 0, y: 0 }, data: {} }] });
    expect(parseFragment(text)).toEqual({ ok: false, error: 'invalidNode' });
  });

  it('nodeWithoutPosition_returnsError', () => {
    const text = JSON.stringify({ nodes: [{ id: 'a', type: 'activity', data: {} }] });
    expect(parseFragment(text)).toEqual({ ok: false, error: 'invalidNode' });
  });

  it('duplicateNodeIds_returnsError', () => {
    const text = JSON.stringify({ nodes: [node('a'), node('a')] });
    expect(parseFragment(text)).toEqual({ ok: false, error: 'duplicateId' });
  });

  it('fullDefinition_isAccepted', () => {
    // A complete workflow definition has the same shape, so it can be pasted as well.
    const text = JSON.stringify({ nodes: [node('a'), node('b')], edges: [edge('ab', 'a', 'b')] });

    const result = parseFragment(text);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.fragment.edges).toHaveLength(1);
      expect(result.droppedEdges).toBe(0);
    }
  });

  it('edgesLeavingTheText_areDroppedAndCounted', () => {
    const text = JSON.stringify({ nodes: [node('a')], edges: [edge('ax', 'a', 'x'), { id: 'broken' }] });

    const result = parseFragment(text);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.fragment.edges).toEqual([]);
      expect(result.droppedEdges).toBe(2);
    }
  });

  it('countsMaskedSecretValues', () => {
    // A workflow export or a viewer's copy carries "***" where a secret was.
    const text = JSON.stringify({
      nodes: [node('a', { data: { activityType: 'restApi', config: { apiKey: '***', headers: '***', url: 'x' } } })],
      edges: [],
    });

    const result = parseFragment(text);

    expect(result.ok && result.redactedValues).toBe(2);
  });

  it('stripsRuntimeDataAndCanvasState', () => {
    const text = JSON.stringify({
      nodes: [{ ...node('a'), selected: true, data: { label: 'a', __liveStatus: 'Failed' } }],
    });

    const result = parseFragment(text);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.fragment.nodes[0]).not.toHaveProperty('selected');
      expect(result.fragment.nodes[0].data).toEqual({ label: 'a' });
    }
  });
});

describe('remapFragment', () => {
  it('allocates independent aliases and rewrites internal bindings and decision guards in one pass', () => {
    const fragment = { nodes: [
      node('a', { data: { outputVariable: 'result' } }),
      node('b', { data: { targetMachineId: '{{result.output}}', credentialId: '{{a.param.credential}}', config: {
        cases: [{ name: 'ok', condition: { type: 'comparison', op: '==', left: { kind: 'variable', stepId: 'result', field: 'output' }, right: { kind: 'literal', value: '{{result.output}}' } } }],
      } } }),
    ], edges: [edge('ab', 'a', 'b', { condition: 'result.success' })] };
    const pasted = remapFragment(fragment, { existingGroupIds: NO_GROUPS, existingAliases: new Set(['result_2']), dx: 0, dy: 0 });
    expect(pasted.nodes[0].data.outputVariable).toBe('result_3');
    expect(pasted.nodes[1].data.targetMachineId).toBe('{{result_3.output}}');
    expect(pasted.nodes[1].data.credentialId).toBe(`{{${pasted.nodes[0].id}.param.credential}}`);
    expect(pasted.nodes[1].data.config).toMatchObject({ cases: [{ condition: {
      left: { stepId: 'result_3' }, right: { value: '{{result.output}}' },
    } }] });
    expect(pasted.edges[0].data?.condition).toBe('result_3.success');
    expect(fragment.nodes[0].data.outputVariable).toBe('result');
  });

  it('masks recognized secrets in scalar arrays at the clipboard boundary', () => {
    const token = `ghp_${'A'.repeat(30)}`;
    const serialized = serializeFragment({ nodes: [node('a', { data: { config: { args: [token, ['{{globals.TOKEN}}', token]] } } })], edges: [] });
    expect(serialized).not.toContain(token);
    expect(JSON.parse(serialized).nodes[0].data.config.args).toEqual(['***', ['{{globals.TOKEN}}', '***']]);
  });
  it('givesEveryNodeAndEdgeAFreshId_andRewiresEdges', () => {
    const fragment = selectFragment([node('a'), node('b')], [edge('ab', 'a', 'b')], ['a', 'b']);

    const pasted = remapFragment(fragment, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    const [a, b] = pasted.nodes;
    expect(a.id).not.toBe('a');
    expect(b.id).not.toBe('b');
    expect(pasted.edges[0]).toMatchObject({ source: a.id, target: b.id });
    expect(pasted.edges[0].id).not.toBe('ab');
  });

  it('rewritesTheShortcutCondition', () => {
    // Without this, the pasted edge still names the original step, and the save is rejected
    // once that step is gone or the fragment lands in another workflow.
    const fragment = selectFragment(
      [node('step-a'), node('step-b')],
      [edge('ab', 'step-a', 'step-b', { condition: 'step-a.success' })],
      ['step-a', 'step-b'],
    );

    const pasted = remapFragment(fragment, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    expect(pasted.edges[0].data?.condition).toBe(`${pasted.nodes[0].id}.success`);
  });

  it('rewritesStepIdsInConditionExpressions', () => {
    const expression = {
      type: 'group',
      op: 'AND',
      children: [
        { type: 'comparison', op: '==', left: { kind: 'variable', stepId: 'step-a', field: 'output' }, right: { kind: 'literal', value: 'x' } },
        { type: 'not', child: { type: 'comparison', op: 'isTrue', left: { kind: 'variable', stepId: 'elsewhere', field: 'success' } } },
      ],
    };
    const fragment = selectFragment(
      [node('step-a'), node('step-b')],
      [edge('ab', 'step-a', 'step-b', { conditionExpression: expression })],
      ['step-a', 'step-b'],
    );

    const pasted = remapFragment(fragment, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    const { children } = pasted.edges[0].data?.conditionExpression as { children: Record<string, unknown>[] };
    expect(children[0].left).toMatchObject({ stepId: pasted.nodes[0].id });
    // A step outside the fragment keeps its reference.
    expect((children[1].child as { left: { stepId: string } }).left.stepId).toBe('elsewhere');
  });

  it('rewritesTemplateReferencesInConfig', () => {
    const fragment = selectFragment(
      [node('step-a'), node('step-b', { data: { activityType: 'log', config: { message: '{{step-a.output}} / {{step-x.output}}' } } })],
      [],
      ['step-a', 'step-b'],
    );

    const pasted = remapFragment(fragment, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    const config = pasted.nodes[1].data.config as { message: string };
    expect(config.message).toBe(`{{${pasted.nodes[0].id}.output}} / {{step-x.output}}`);
  });

  it('movesTopLevelNodes_butNotChildrenOfAPastedGroup', () => {
    const group = node('g', { type: 'group', position: { x: 10, y: 10 } });
    const child = node('c', { parentId: 'g', position: { x: 5, y: 5 } });

    const pasted = remapFragment({ nodes: [child, group], edges: [] }, { existingGroupIds: NO_GROUPS, dx: 40, dy: 40 });

    // Groups come first, as React Flow needs a parent before its children.
    expect(pasted.nodes[0].type).toBe('group');
    expect(pasted.nodes[0].position).toEqual({ x: 50, y: 50 });
    expect(pasted.nodes[1]).toMatchObject({ parentId: pasted.nodes[0].id, position: { x: 5, y: 5 } });
  });

  it('childOfAnExistingGroup_staysInIt', () => {
    const child = node('c', { parentId: 'g-existing', position: { x: 5, y: 5 } });

    const pasted = remapFragment({ nodes: [child], edges: [] }, { existingGroupIds: new Set(['g-existing']), dx: 40, dy: 40 });

    expect(pasted.nodes[0]).toMatchObject({ parentId: 'g-existing', position: { x: 45, y: 45 } });
  });

  it('childOfAnUnknownGroup_becomesTopLevel', () => {
    const child = node('c', { parentId: 'g-elsewhere', extent: 'parent' });

    const pasted = remapFragment({ nodes: [child], edges: [] }, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    expect(pasted.nodes[0]).not.toHaveProperty('parentId');
    expect(pasted.nodes[0]).not.toHaveProperty('extent');
  });

  it('groupNeverGetsAParent', () => {
    // The designer does not nest groups; pasted text must not create a parent cycle.
    const a = node('ga', { type: 'group', parentId: 'gb' });
    const b = node('gb', { type: 'group', parentId: 'ga' });

    const pasted = remapFragment({ nodes: [a, b], edges: [] }, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    expect(pasted.nodes.every((n) => n.parentId === undefined)).toBe(true);
  });

  it('usesTypeSpecificIdPrefixes_andSelectsTheResult', () => {
    const pasted = remapFragment({
      nodes: [node('a'), node('g', { type: 'group' }), node('n', { type: 'stickyNote' })],
      edges: [],
    }, { existingGroupIds: NO_GROUPS, dx: 0, dy: 0 });

    expect(pasted.nodes.map((n) => n.id.split('-')[0]).sort()).toEqual(['group', 'note', 'step']);
    expect(pasted.nodes.every((n) => n.selected)).toBe(true);
  });
});

describe('remapConditionStep', () => {
  const idMap = new Map([['step-a', 'step-new']]);

  it('rewritesAKnownStep', () => {
    expect(remapConditionStep('step-a.failed', idMap)).toBe('step-new.failed');
  });

  it('leavesUnknownStepsAndPlainValuesAlone', () => {
    expect(remapConditionStep('step-x.success', idMap)).toBe('step-x.success');
    expect(remapConditionStep('always', idMap)).toBe('always');
    expect(remapConditionStep(undefined, idMap)).toBe('');
  });
});
