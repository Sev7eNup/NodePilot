import { describe, it, expect } from 'vitest';
import type { Node } from '@xyflow/react';
import {
  findLastSimilarNode,
  extractBaseUrl,
  getSmartDefaults,
} from '../../lib/lastSimilarNode';

function n(id: string, x: number, y: number, activityType: string, extra: Record<string, unknown> = {}): Node {
  return {
    id,
    type: 'activity',
    position: { x, y },
    data: { activityType, label: id, ...extra },
  } as unknown as Node;
}

describe('findLastSimilarNode', () => {
  it('returns undefined when no node matches', () => {
    expect(findLastSimilarNode([], 'runScript')).toBeUndefined();
    expect(findLastSimilarNode([n('a', 0, 0, 'restApi')], 'runScript')).toBeUndefined();
  });

  it('returns the rightmost (largest x) node of the matching type', () => {
    const nodes = [
      n('a', 0, 0, 'runScript'),
      n('b', 300, 0, 'runScript'),
      n('c', 600, 0, 'runScript'),
      n('d', 1000, 0, 'restApi'), // wrong type — must be skipped
    ];
    expect(findLastSimilarNode(nodes, 'runScript')!.id).toBe('c');
  });

  it('breaks ties on x by larger y', () => {
    const nodes = [
      n('top', 500, 100, 'runScript'),
      n('bottom', 500, 400, 'runScript'),
    ];
    expect(findLastSimilarNode(nodes, 'runScript')!.id).toBe('bottom');
  });

  it('falls back to last array index when both x and y are identical', () => {
    const nodes = [
      n('first', 0, 0, 'runScript'),
      n('second', 0, 0, 'runScript'),
    ];
    expect(findLastSimilarNode(nodes, 'runScript')!.id).toBe('second');
  });

  it('ignores other activity types entirely', () => {
    const nodes = [
      n('rs1', 100, 0, 'runScript'),
      n('api1', 800, 0, 'restApi'),       // further right but wrong type
      n('rs2', 400, 0, 'runScript'),
    ];
    expect(findLastSimilarNode(nodes, 'runScript')!.id).toBe('rs2');
  });
});

describe('extractBaseUrl', () => {
  it('returns empty for empty input', () => {
    expect(extractBaseUrl('')).toBe('');
  });

  it('keeps a fully templated URL verbatim', () => {
    expect(extractBaseUrl('{{baseUrl}}')).toBe('{{baseUrl}}');
  });

  it('keeps scheme + host + first path segment', () => {
    expect(extractBaseUrl('https://api.example.com/v1/users')).toBe('https://api.example.com/v1');
    expect(extractBaseUrl('http://api.example.com/api/products/42')).toBe('http://api.example.com/api');
  });

  it('keeps scheme + host when no path is present', () => {
    expect(extractBaseUrl('https://api.example.com')).toBe('https://api.example.com');
    expect(extractBaseUrl('https://api.example.com/')).toBe('https://api.example.com/');
  });

  it('strips query string and fragment', () => {
    expect(extractBaseUrl('https://api.example.com/v1/users?id=5')).toBe('https://api.example.com/v1');
    expect(extractBaseUrl('https://api.example.com/v1#section')).toBe('https://api.example.com/v1');
  });

  it('strips a path segment containing template placeholders', () => {
    expect(extractBaseUrl('https://api.example.com/users/{{id}}')).toBe('https://api.example.com/users');
  });

  it('returns relative URL prefix when no scheme is present', () => {
    expect(extractBaseUrl('/api/internal/resource')).toBe('/api/internal');
    expect(extractBaseUrl('/api')).toBe('/api');
  });
});

describe('getSmartDefaults', () => {
  it('returns empty object when no similar node exists', () => {
    expect(getSmartDefaults('runScript', [])).toEqual({});
  });

  it('inherits machine + credential for runScript', () => {
    const nodes = [
      n('s1', 0, 0, 'runScript', { targetMachineId: 'machine-A', credentialId: 'cred-1' }),
    ];
    expect(getSmartDefaults('runScript', nodes)).toEqual({
      targetMachineId: 'machine-A',
      credentialId: 'cred-1',
    });
  });

  it('inherits machine + credential for serviceManagement', () => {
    const nodes = [
      n('s1', 0, 0, 'serviceManagement', { targetMachineId: 'machine-X', credentialId: null }),
    ];
    expect(getSmartDefaults('serviceManagement', nodes)).toEqual({
      targetMachineId: 'machine-X',
      credentialId: null,
    });
  });

  it('uses the rightmost runScript when multiple exist', () => {
    const nodes = [
      n('left', 0, 0, 'runScript', { targetMachineId: 'older', credentialId: null }),
      n('right', 800, 0, 'runScript', { targetMachineId: 'newer', credentialId: 'cred-2' }),
    ];
    expect(getSmartDefaults('runScript', nodes)).toEqual({
      targetMachineId: 'newer',
      credentialId: 'cred-2',
    });
  });

  it('extracts base URL for restApi', () => {
    const nodes = [
      n('api1', 0, 0, 'restApi', { config: { url: 'https://api.example.com/v1/users' } }),
    ];
    expect(getSmartDefaults('restApi', nodes)).toEqual({ config: { url: 'https://api.example.com/v1' } });
  });

  it('returns empty for restApi when last has no URL', () => {
    const nodes = [n('api1', 0, 0, 'restApi', { config: {} })];
    expect(getSmartDefaults('restApi', nodes)).toEqual({});
  });

  it('inherits provider + connectionRef for sql', () => {
    const nodes = [
      n('s1', 0, 0, 'sql', { config: { provider: 'postgres', connectionRef: 'reportsDb', query: 'SELECT 1' } }),
    ];
    expect(getSmartDefaults('sql', nodes)).toEqual({
      config: { provider: 'postgres', connectionRef: 'reportsDb' },
    });
  });

  it('inherits isHtml for emailNotification', () => {
    const nodes = [n('e1', 0, 0, 'emailNotification', { config: { isHtml: true } })];
    expect(getSmartDefaults('emailNotification', nodes)).toEqual({ config: { isHtml: true } });
  });

  it('returns empty for activity types without a smart-default rule', () => {
    const nodes = [n('d1', 0, 0, 'delay', { config: { seconds: 30 } })];
    expect(getSmartDefaults('delay', nodes)).toEqual({});
  });

  it('returns empty for triggers (no smart defaults)', () => {
    const nodes = [n('t1', 0, 0, 'manualTrigger', { config: { parameters: [] } })];
    expect(getSmartDefaults('manualTrigger', nodes)).toEqual({});
  });
});
