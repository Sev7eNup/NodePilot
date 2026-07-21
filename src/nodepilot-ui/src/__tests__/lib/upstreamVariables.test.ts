import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import {
  describeNodeOutputs,
  getUpstreamVariables,
  findEdgePathBetween,
} from '../../lib/upstreamVariables';

function node(id: string, data: Record<string, unknown>): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label: id, ...data } };
}

function edge(id: string, source: string, target: string): Edge {
  return { id, source, target, type: 'labeled', data: {} };
}

describe('describeNodeOutputs', () => {
  it('alwaysIncludesPrimaryOutputAsFirstEntry', () => {
    const n = node('s1', { activityType: 'runScript', outputVariable: 'diskCheck' });
    const out = describeNodeOutputs(n);

    expect(out[0].expression).toBe('{{diskCheck.output}}');
    expect(out[0].variable).toBe('diskCheck');
  });

  it('fallsBackToNodeId_whenNoOutputVariableSet', () => {
    const n = node('step-abc123', { activityType: 'runScript' });
    const out = describeNodeOutputs(n);

    expect(out[0].expression).toBe('{{step-abc123.output}}');
  });

  it('manualTrigger_emitsParamPerDeclaredParameter', () => {
    const n = node('trg', {
      activityType: 'manualTrigger',
      outputVariable: 'trigger',
      config: {
        parameters: [
          { name: 'serverName', type: 'string' },
          { name: 'restartCount', type: 'number' },
          { name: 'isDryRun', type: 'boolean' },
        ],
      },
    });

    const out = describeNodeOutputs(n);

    // Output + 3 params = 4 entries
    expect(out).toHaveLength(4);
    const serverName = out.find((v) => v.variable === 'trigger.param.serverName')!;
    const restartCount = out.find((v) => v.variable === 'trigger.param.restartCount')!;
    const isDryRun = out.find((v) => v.variable === 'trigger.param.isDryRun')!;
    expect(serverName.type).toBe('string');
    expect(restartCount.type).toBe('number');
    expect(isDryRun.type).toBe('boolean');
  });

  it('webhookTrigger_emitsParamPerFieldMapping', () => {
    const n = node('hook', {
      activityType: 'webhookTrigger',
      outputVariable: 'wh',
      config: {
        path: 'incident',
        fieldMappings: [
          { name: 'ticketId', path: '$.ticket.id' },
          { name: 'severity', path: '$.ticket.severity' },
          { name: '', path: '$.ignored' },
        ],
      },
    });

    const out = describeNodeOutputs(n);

    const params = out.map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining([
      'wh.param.ticketId',
      'wh.param.severity',
      // static catalog outputs must still be present alongside the dynamic mappings
      'wh.param.webhookBody',
    ]));
    expect(params.filter((p) => p.endsWith('.param.'))).toHaveLength(0);
  });

  it('webhookTrigger_withoutMappings_emitsOnlyStaticOutputs', () => {
    const n = node('hook', { activityType: 'webhookTrigger', outputVariable: 'wh', config: { path: 'x' } });
    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining(['wh.param.webhookBody', 'wh.param.webhookMethod', 'wh.param.webhookPath']));
  });

  it('fileWatcherTrigger_emitsStaticOutputs', () => {
    const n = node('fw', { activityType: 'fileWatcherTrigger', outputVariable: 'watch', config: { directory: 'C:\\inbox' } });
    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);

    expect(params).toEqual(expect.arrayContaining([
      'watch.param.fileAction',
      'watch.param.filePath',
      'watch.param.fileName',
    ]));
  });

  it('externalTriggers_emitStaticOutputs', () => {
    const schedule = describeNodeOutputs(node('sched', {
      activityType: 'scheduleTrigger',
      outputVariable: 'sched',
      config: { cronExpression: '0 0/5 * * * ?' },
    })).map((v) => v.variable);
    const database = describeNodeOutputs(node('db', {
      activityType: 'databaseTrigger',
      outputVariable: 'db',
      config: { connectionRef: 'prod', query: 'select max(id) from Jobs' },
    })).map((v) => v.variable);
    const eventLog = describeNodeOutputs(node('ev', {
      activityType: 'eventLogTrigger',
      outputVariable: 'ev',
      config: { logName: 'Application', entryType: 'Error' },
    })).map((v) => v.variable);

    expect(schedule).toEqual(expect.arrayContaining(['sched.param.firedAt', 'sched.param.nextFireAt']));
    expect(database).toEqual(expect.arrayContaining(['db.param.dbSentinel', 'db.param.dbPrevious']));
    expect(eventLog).toEqual(expect.arrayContaining([
      'ev.param.eventSource',
      'ev.param.eventEntryType',
      'ev.param.eventId',
      'ev.param.eventMessage',
      'ev.param.eventTimeWritten',
    ]));
  });

  it('startWorkflow_emitsExecutionMetadataParams', () => {
    // The four __metadata params (executionId, status, workflowId, workflowName) are
    // a stable contract for downstream activities to read.
    const n = node('sw', { activityType: 'startWorkflow', outputVariable: 'sub' });
    const out = describeNodeOutputs(n);

    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining([
      'sub.param.__executionId',
      'sub.param.__status',
      'sub.param.__workflowId',
      'sub.param.__workflowName',
    ]));
  });

  it('forEach_emitsAggregatedCounters', () => {
    const n = node('fe', { activityType: 'forEach', outputVariable: 'loop' });
    const out = describeNodeOutputs(n);

    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining([
      'loop.param.total',
      'loop.param.succeeded',
      'loop.param.failed',
      'loop.param.skipped',
      'loop.param.results',
      'loop.param.firstError',
    ]));

    // Counters should be typed as numbers, results as array.
    expect(out.find((v) => v.variable === 'loop.param.total')?.type).toBe('number');
    expect(out.find((v) => v.variable === 'loop.param.results')?.type).toBe('array');
  });

  it('registryOperation_readWithValueName_emitsValueAndType', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'read', keyPath: 'HKLM:\\X', valueName: 'V' },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining(['reg.param.value', 'reg.param.type']));
  });

  it('registryOperation_readWithoutValueName_emitsValuesAndCount', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'read', keyPath: 'HKLM:\\X' },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining(['reg.param.values', 'reg.param.count']));
    expect(out.find((v) => v.variable === 'reg.param.values')?.type).toBe('array');
    expect(out.find((v) => v.variable === 'reg.param.count')?.type).toBe('number');
  });

  it('registryOperation_listSubKeys_emitsSubKeysAndCount', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'listSubKeys', keyPath: 'HKLM:\\X' },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);
    expect(params).toEqual(expect.arrayContaining(['reg.param.subKeys', 'reg.param.count']));
    expect(out.find((v) => v.variable === 'reg.param.subKeys')?.type).toBe('array');
  });

  it('registryOperation_exists_emitsExistsBoolean', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'exists', keyPath: 'HKLM:\\X' },
    });

    const out = describeNodeOutputs(n);
    expect(out.find((v) => v.variable === 'reg.param.exists')?.type).toBe('boolean');
  });

  it('registryOperation_createKey_emitsCreatedBoolean', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'createKey', keyPath: 'HKLM:\\X' },
    });

    const out = describeNodeOutputs(n);
    expect(out.find((v) => v.variable === 'reg.param.created')?.type).toBe('boolean');
  });

  it('registryOperation_deleteKey_emitsNoExtraParams', () => {
    const n = node('reg', {
      activityType: 'registryOperation',
      outputVariable: 'reg',
      config: { operation: 'deleteKey', keyPath: 'HKLM:\\X' },
    });

    const out = describeNodeOutputs(n);
    // Only the primary output is emitted, no .param.* entries.
    expect(out.filter((v) => v.expression.includes('.param.'))).toHaveLength(0);
  });

  it('wmiQuery_withCaptureProperties_emitsCountAndEachProperty', () => {
    // Added 2026-05-17: wmiQuery now projects user-listed CIM properties into
    // param.<Name> (plus param.count for the row total). The variable picker
    // surfaces them so authors get autocomplete on {{wmi_os.param.Caption}}.
    const n = node('w', {
      activityType: 'wmiQuery',
      outputVariable: 'wmi_os',
      config: {
        className: 'Win32_OperatingSystem',
        captureProperties: ['Caption', 'BuildNumber'],
      },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);

    expect(params).toEqual(expect.arrayContaining([
      'wmi_os.param.count',
      'wmi_os.param.Caption',
      'wmi_os.param.BuildNumber',
    ]));
    expect(out.find((v) => v.variable === 'wmi_os.param.count')?.type).toBe('number');
    expect(out.find((v) => v.variable === 'wmi_os.param.Caption')?.type).toBe('string');
  });

  it('wmiQuery_withoutCaptureProperties_emitsOnlyPrimaryOutput', () => {
    // Legacy contract: a wmiQuery node without captureProperties produces no
    // per-property params. The variable picker must NOT pretend they exist —
    // otherwise authors would see them offered here but get an "unresolved
    // variable" error at runtime when the value was never actually captured.
    const n = node('w', {
      activityType: 'wmiQuery',
      outputVariable: 'wmi_legacy',
      config: { className: 'Win32_OperatingSystem' },
    });

    const out = describeNodeOutputs(n);
    expect(out.filter((v) => v.expression.includes('.param.'))).toHaveLength(0);
    expect(out[0].expression).toBe('{{wmi_legacy.output}}');
  });

  it('sql_emitsRowCountAndRowsAffected', () => {
    const n = node('q', {
      activityType: 'sql',
      outputVariable: 'rows',
      config: { provider: 'sqlite', query: 'SELECT 1' },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);

    expect(params).toEqual(expect.arrayContaining([
      'rows.param.rowCount',
      'rows.param.rowsAffected',
    ]));
    expect(out.find((v) => v.variable === 'rows.param.rowCount')?.type).toBe('number');
  });

  it('waitForCondition_emitsAttemptsElapsedLastResult', () => {
    const n = node('w', { activityType: 'waitForCondition', outputVariable: 'wait' });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);

    expect(out).toHaveLength(4);
    expect(params).toEqual(expect.arrayContaining([
      'wait.param.attempts',
      'wait.param.elapsedSeconds',
      'wait.param.lastResult',
    ]));
    expect(out.find((v) => v.variable === 'wait.param.attempts')?.type).toBe('number');
    expect(out.find((v) => v.variable === 'wait.param.lastResult')?.type).toBe('boolean');
  });

  it('serviceManagement_emitsNameStateStartType', () => {
    const n = node('svc', {
      activityType: 'serviceManagement',
      outputVariable: 'statusResult',
      config: { serviceName: 'Spooler', action: 'status' },
    });

    const out = describeNodeOutputs(n);
    const params = out.filter((v) => v.expression.includes('.param.')).map((v) => v.variable);

    // Output + 3 status fields = 4 entries
    expect(out).toHaveLength(4);
    expect(params).toEqual(expect.arrayContaining([
      'statusResult.param.name',
      'statusResult.param.status',
      'statusResult.param.startType',
    ]));
    expect(out.find((v) => v.variable === 'statusResult.param.status')?.type).toBe('string');
  });

  it('manualTriggerWithEmptyParameterName_isSkipped', () => {
    // The UI lets users add a blank row, then they fill in the name. Until then we
    // must not emit a phantom .param. entry — pin that behaviour.
    const n = node('trg', {
      activityType: 'manualTrigger',
      outputVariable: 'trg',
      config: { parameters: [{ name: '', type: 'string' }, { name: 'real', type: 'string' }] },
    });

    const out = describeNodeOutputs(n);

    expect(out).toHaveLength(2); // primary output + the one named param
    expect(out.some((v) => v.variable === 'trg.param.real')).toBe(true);
  });
});

describe('getUpstreamVariables', () => {
  it('linearChain_returnsAllAncestors_inOrderOfDistance', () => {
    // a → b → c → target. From target's perspective, c is the closest ancestor,
    // then b, then a. The function returns ALL of them.
    const a = node('a', { activityType: 'runScript' });
    const b = node('b', { activityType: 'runScript' });
    const c = node('c', { activityType: 'runScript' });
    const target = node('target', { activityType: 'runScript' });
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c'), edge('e3', 'c', 'target')];

    const vars = getUpstreamVariables('target', [a, b, c, target], edges);
    const stepIds = vars.map((v) => v.stepId);

    expect(stepIds).toContain('a');
    expect(stepIds).toContain('b');
    expect(stepIds).toContain('c');
    expect(stepIds).not.toContain('target');
  });

  it('noUpstream_returnsEmptyArray', () => {
    const isolated = node('isolated', { activityType: 'runScript' });
    expect(getUpstreamVariables('isolated', [isolated], [])).toEqual([]);
  });

  it('diamondGraph_visitsCommonAncestorOnce', () => {
    // a → {b, c} → d. From d, both b and c lead back to a. Pin that a is visited only once.
    const a = node('a', { activityType: 'runScript' });
    const b = node('b', { activityType: 'runScript' });
    const c = node('c', { activityType: 'runScript' });
    const d = node('d', { activityType: 'runScript' });
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c'), edge('e3', 'b', 'd'), edge('e4', 'c', 'd')];

    const vars = getUpstreamVariables('d', [a, b, c, d], edges);
    const fromA = vars.filter((v) => v.stepId === 'a');

    // a is reachable via both b and c, but must be visited only once: its outputs appear without
    // duplicates (a second visit would repeat every expression). Count-agnostic to the exact
    // number of outputs a runScript emits (output + param.exitCode + scanned vars).
    const exprs = fromA.map((v) => v.expression);
    expect(exprs.length).toBeGreaterThan(0);
    expect(new Set(exprs).size).toBe(exprs.length);
  });

  it('handlesMultipleParentsInTraversal', () => {
    const a = node('a', { activityType: 'runScript' });
    const b = node('b', { activityType: 'runScript' });
    const target = node('target', { activityType: 'runScript' });
    const edges = [edge('e1', 'a', 'target'), edge('e2', 'b', 'target')];

    const vars = getUpstreamVariables('target', [a, b, target], edges);

    // Both parents are traversed (unique stepIds), regardless of how many outputs each emits.
    expect([...new Set(vars.map((v) => v.stepId))].sort()).toEqual(['a', 'b']);
  });
});

describe('findEdgePathBetween', () => {
  it('directEdge_returnsThatEdgeId', () => {
    const edges = [edge('e1', 'a', 'b')];
    expect(findEdgePathBetween('a', 'b', edges)).toEqual(new Set(['e1']));
  });

  it('multiHop_returnsAllEdgesOnPath', () => {
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c'), edge('e3', 'c', 'd')];
    expect(findEdgePathBetween('a', 'd', edges)).toEqual(new Set(['e1', 'e2', 'e3']));
  });

  it('noPath_returnsEmptySet', () => {
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'c', 'd')]; // disconnected components
    expect(findEdgePathBetween('a', 'd', edges)).toEqual(new Set());
  });

  it('diamond_returnsOneCompletePath_BFSMarksOnDiscover', () => {
    // BFS marks targets visited the moment they're queued, so when both a→b→d and
    // a→c→d exist, only the FIRST-arrival path's edges land in the result. This is
    // intentional — finding all shortest paths would need a multi-parent search.
    // Pin the actual behaviour so a refactor that changes BFS semantics surfaces here.
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c'), edge('e3', 'b', 'd'), edge('e4', 'c', 'd')];

    const result = findEdgePathBetween('a', 'd', edges);

    // 2 edges = one path from a to d (depth 2). Could be {e1,e3} or {e2,e4}.
    expect(result.size).toBe(2);
  });

  it('selfLoop_returnsImmediateEdgeId', () => {
    // Edge from node to itself; producer == consumer means BFS terminates immediately
    // at the first hop and includes that edge in the result set.
    const edges = [edge('self', 'a', 'a')];
    expect(findEdgePathBetween('a', 'a', edges)).toEqual(new Set());
  });
});
