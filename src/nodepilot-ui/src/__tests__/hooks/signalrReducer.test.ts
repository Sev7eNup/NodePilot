import { describe, it, expect } from 'vitest';
import {
  applyLiveEvents,
  classifyEntry,
  isActiveExecution,
  mergeHydrated,
  mergeListingOnly,
  normalizeBatchItem,
  sortLiveExecutions,
  trimLiveExecutions,
} from '../../hooks/signalrReducer';
import type { LiveExecution, LiveExecutionsById, StepUpdate } from '../../hooks/signalrTypes';

const baseExec = (overrides: Partial<LiveExecution> = {}): LiveExecution => ({
  executionId: 'e1',
  workflowId: 'wf1',
  status: 'Running',
  steps: [],
  startedAt: '2026-05-05T12:00:00Z',
  databus: {},
  ...overrides,
});

const step = (overrides: Partial<StepUpdate> = {}): StepUpdate => ({
  executionId: 'e1',
  workflowId: 'wf1',
  stepId: 's1',
  stepType: 'log',
  status: 'Running',
  ...overrides,
});

describe('signalrReducer.isActiveExecution', () => {
  it('treats Running and Pending as active regardless of step state', () => {
    expect(isActiveExecution(baseExec({ status: 'Running' }))).toBe(true);
    expect(isActiveExecution(baseExec({ status: 'Pending' }))).toBe(true);
  });

  it('treats Succeeded as active when a step is still Running (channel-drop recovery)', () => {
    expect(isActiveExecution(baseExec({ status: 'Succeeded', steps: [step({ status: 'Running' })] }))).toBe(true);
  });

  it('treats Succeeded with all-terminal steps as inactive', () => {
    expect(isActiveExecution(baseExec({ status: 'Succeeded', steps: [step({ status: 'Succeeded' })] }))).toBe(false);
  });

  it('treats Paused steps as active', () => {
    expect(isActiveExecution(baseExec({ status: 'Failed', steps: [step({ status: 'Paused' })] }))).toBe(true);
  });
});

describe('signalrReducer.sortLiveExecutions', () => {
  it('puts paused executions first, then active, then completed (newest first within group)', () => {
    const paused = baseExec({ executionId: 'p', startedAt: '2026-05-05T12:00:00Z', steps: [step({ status: 'Paused' })] });
    const activeOld = baseExec({ executionId: 'a-old', startedAt: '2026-05-05T11:00:00Z', status: 'Running' });
    const activeNew = baseExec({ executionId: 'a-new', startedAt: '2026-05-05T11:30:00Z', status: 'Running' });
    const done = baseExec({ executionId: 'd', startedAt: '2026-05-05T13:00:00Z', status: 'Succeeded', steps: [step({ status: 'Succeeded' })] });

    const sorted = sortLiveExecutions([done, activeOld, paused, activeNew]);

    expect(sorted.map((e) => e.executionId)).toEqual(['p', 'a-new', 'a-old', 'd']);
  });
});

describe('signalrReducer.trimLiveExecutions', () => {
  it('keeps all active runs and caps completed at 50', () => {
    const state: LiveExecutionsById = {};
    for (let i = 0; i < 100; i++) {
      const id = `done-${i}`;
      state[id] = baseExec({
        executionId: id,
        status: 'Succeeded',
        startedAt: new Date(2026, 0, 1, 0, i).toISOString(),
        steps: [step({ status: 'Succeeded' })],
      });
    }
    for (let i = 0; i < 5; i++) {
      const id = `active-${i}`;
      state[id] = baseExec({ executionId: id, status: 'Running' });
    }

    const trimmed = trimLiveExecutions(state);
    const allValues = Object.values(trimmed);

    expect(allValues.filter((e) => e.status === 'Running')).toHaveLength(5);
    expect(allValues.filter((e) => e.status === 'Succeeded')).toHaveLength(50);
  });

  it('returns the original object when nothing needs trimming', () => {
    const state: LiveExecutionsById = { e1: baseExec() };
    expect(trimLiveExecutions(state)).toBe(state);
  });
});

describe('signalrReducer.mergeListingOnly', () => {
  it('inserts new executions as listing-only entries with empty steps', () => {
    const next = mergeListingOnly([
      { id: 'new1', workflowId: 'wf1', status: 'Running', startedAt: '2026-05-05T12:00:00Z' },
    ], {});
    expect(next.new1.steps).toEqual([]);
    expect(next.new1.status).toBe('Running');
  });

  it('updates terminal status on existing entries but preserves their steps', () => {
    const prev: LiveExecutionsById = {
      keep: baseExec({ executionId: 'keep', status: 'Running', steps: [step({ status: 'Succeeded' })] }),
    };
    const next = mergeListingOnly([
      { id: 'keep', workflowId: 'wf1', status: 'Succeeded', startedAt: '2026-05-05T12:00:00Z', completedAt: '2026-05-05T12:05:00Z' },
    ], prev);

    expect(next.keep.status).toBe('Succeeded');
    expect(next.keep.completedAt).toBe('2026-05-05T12:05:00Z');
    expect(next.keep.steps).toHaveLength(1);
  });

  it('does NOT overwrite a non-terminal listing onto an existing entry', () => {
    const prev: LiveExecutionsById = {
      keep: baseExec({ executionId: 'keep', status: 'Succeeded' }),
    };
    const next = mergeListingOnly([
      { id: 'keep', workflowId: 'wf1', status: 'Running', startedAt: '2026-05-05T12:00:00Z' },
    ], prev);
    expect(next.keep.status).toBe('Succeeded');
  });
});

describe('signalrReducer.mergeHydrated', () => {
  it('merges hydrated databus entries without overwriting live entries', () => {
    const hydrated: LiveExecutionsById = {
      e1: baseExec({
        databus: {
          's1.param.freeGb': { value: '42', stepId: 's1', kind: 'param', paramKey: 'freeGb' },
        },
      }),
    };
    const prev: LiveExecutionsById = {
      e1: baseExec({
        databus: {
          's1.param.freeGb': { value: '43', stepId: 's1', kind: 'param', paramKey: 'freeGb' },
          's1.param.status': { value: 'ok', stepId: 's1', kind: 'param', paramKey: 'status' },
        },
      }),
    };

    const next = mergeHydrated(hydrated, prev);

    expect(next.e1.databus['s1.param.freeGb'].value).toBe('43');
    expect(next.e1.databus['s1.param.status'].value).toBe('ok');
  });
});

describe('signalrReducer.classifyEntry', () => {
  const stepNames = new Map<string, string | null | undefined>([['step-1', 'My Step']]);

  it('classifies manual.* keys as trigger-input', () => {
    // Engine only populates manual.* as a trigger-input namespace. Webhook payload lives
    // under manual.webhookBody / manual.webhookHeader_X — the pseudo "webhook.*" /
    // "trigger.*" namespaces never appear in the real variables dict.
    expect(classifyEntry('manual.x', 'v', stepNames).kind).toBe('trigger');
    expect(classifyEntry('manual.webhookBody', '{}', stepNames).kind).toBe('trigger');
  });

  it('classifies globals.*', () => {
    const entry = classifyEntry('globals.MyGlobal', 'value', stepNames);
    expect(entry.kind).toBe('global');
  });

  it('classifies stepId.param.NAME with the step name resolved', () => {
    const entry = classifyEntry('step-1.param.foo', 'bar', stepNames);
    expect(entry.kind).toBe('param');
    expect(entry.stepId).toBe('step-1');
    expect(entry.stepName).toBe('My Step');
    expect(entry.paramKey).toBe('foo');
  });

  it('classifies stepId.output and stepId.error', () => {
    expect(classifyEntry('step-1.output', 'x', stepNames).kind).toBe('output');
    expect(classifyEntry('step-1.error', 'x', stepNames).kind).toBe('error');
  });

  it('falls back to "other" for unknown shapes', () => {
    expect(classifyEntry('weird-key', 'v', stepNames).kind).toBe('other');
  });
});

describe('signalrReducer.applyLiveEvents', () => {
  it('returns prev unchanged when events is empty', () => {
    const prev: LiveExecutionsById = { e1: baseExec() };
    expect(applyLiveEvents(prev, [])).toBe(prev);
  });

  it('StepStarted on a known exec adds the step and bumps status to Running', () => {
    const prev: LiveExecutionsById = {
      e1: baseExec({ status: 'Pending', steps: [] }),
    };
    const next = applyLiveEvents(prev, [
      { type: 'StepStarted', evt: { executionId: 'e1', workflowId: 'wf1', stepId: 's1', stepType: 'log', startedAt: '2026-05-05T12:01:00Z' } },
    ]);
    expect(next.e1.status).toBe('Running');
    expect(next.e1.steps.map((s) => s.stepId)).toEqual(['s1']);
  });

  it('StepCompleted promotes existing step to terminal and merges output params into databus', () => {
    const prev: LiveExecutionsById = {
      e1: baseExec({ steps: [step({ status: 'Running' })] }),
    };
    const next = applyLiveEvents(prev, [
      {
        type: 'StepCompleted',
        evt: {
          executionId: 'e1',
          workflowId: 'wf1',
          stepId: 's1',
          status: 'Succeeded',
          completedAt: '2026-05-05T12:02:00Z',
          outputParameters: { foo: 'bar' },
        },
      },
    ]);
    expect(next.e1.steps[0].status).toBe('Succeeded');
    expect(next.e1.databus['s1.param.foo'].value).toBe('bar');
  });

  it('StepCompleted writes .output and .error databus entries (live parity with engine vars-dict)', () => {
    // Regression: before this fix, only .param.* was written to the databus — running
    // workflows therefore showed a different view than paused ones (StepPaused dumps the
    // full variables snapshot). During a run, the live preview and expression tester saw
    // empty `{{s1.output}}` tooltips even though stdout was actually available.
    const prev: LiveExecutionsById = {
      e1: baseExec({ steps: [step({ status: 'Running' })] }),
    };
    const next = applyLiveEvents(prev, [
      {
        type: 'StepCompleted',
        evt: {
          executionId: 'e1',
          workflowId: 'wf1',
          stepId: 's1',
          status: 'Succeeded',
          output: 'free=42G',
          errorOutput: 'warning: low',
          completedAt: '2026-05-05T12:02:00Z',
        },
      },
    ]);
    expect(next.e1.databus['s1.output'].value).toBe('free=42G');
    expect(next.e1.databus['s1.output'].kind).toBe('output');
    expect(next.e1.databus['s1.error'].value).toBe('warning: low');
    expect(next.e1.databus['s1.error'].kind).toBe('error');
  });

  it('buildDatabusFromHydratedSteps rebuilds entries from REST snapshot', async () => {
    // Regression: after a browser refresh, exec.databus was always empty because
    // hydrateStepsForExecution only merged step status/output. Param tooltips for terminal
    // runs went blank even though the data is right there in the /steps endpoint. This
    // helper must build the same keys from outputParametersJson + output/error that a
    // live StepCompleted event would produce.
    const { buildDatabusFromHydratedSteps } = await import('../../hooks/signalrReducer');
    const databus = buildDatabusFromHydratedSteps([
      {
        stepId: 's1',
        stepName: 'Disk Check',
        output: 'free=42G',
        errorOutput: null,
        outputParametersJson: JSON.stringify({ host: 'web01', freeGb: '42' }),
        outputVariable: 'diskCheck',
      },
    ]);
    expect(databus['s1.output'].value).toBe('free=42G');
    expect(databus['diskCheck.output'].value).toBe('free=42G');
    expect(databus['s1.param.host'].value).toBe('web01');
    expect(databus['diskCheck.param.host'].value).toBe('web01');
    expect(databus['s1.param.freeGb'].value).toBe('42');
    expect(databus['diskCheck.param.freeGb'].value).toBe('42');
  });

  it('buildDatabusFromHydratedSteps tolerates malformed outputParametersJson', async () => {
    // Hydration must never throw — a corrupted/legacy row blanking the whole live view
    // would be worse than missing param entries for that row.
    const { buildDatabusFromHydratedSteps } = await import('../../hooks/signalrReducer');
    const databus = buildDatabusFromHydratedSteps([
      { stepId: 's1', output: 'ok', errorOutput: null, outputParametersJson: '{not json' },
    ]);
    expect(databus['s1.output'].value).toBe('ok');
    // No s1.param.* entries — but the call still succeeded.
    expect(Object.keys(databus).filter((k) => k.startsWith('s1.param.'))).toHaveLength(0);
  });

  it('StepCompleted with outputVariable mirrors entries under the alias head', () => {
    // Engine BuildStepVariables exposes the same value under both {stepId}.output and
    // {alias}.output. The live databus must match — otherwise a downstream node's
    // `{{diskCheck.output}}` template lights up as "unresolved" in the preview overlay
    // while the engine itself would happily substitute it.
    const prev: LiveExecutionsById = {
      e1: baseExec({ steps: [step({ status: 'Running' })] }),
    };
    const next = applyLiveEvents(prev, [
      {
        type: 'StepCompleted',
        evt: {
          executionId: 'e1',
          workflowId: 'wf1',
          stepId: 's1',
          status: 'Succeeded',
          output: '42',
          completedAt: '2026-05-05T12:02:00Z',
          outputParameters: { host: 'web01' },
          outputVariable: 'diskCheck',
        },
      },
    ]);
    expect(next.e1.databus['s1.output'].value).toBe('42');
    expect(next.e1.databus['diskCheck.output'].value).toBe('42');
    expect(next.e1.databus['s1.param.host'].value).toBe('web01');
    expect(next.e1.databus['diskCheck.param.host'].value).toBe('web01');
  });

  it('ExecutionStatusChanged on terminal flips lingering Running steps to Failed/Skipped', () => {
    const prev: LiveExecutionsById = {
      e1: baseExec({ steps: [step({ status: 'Running' })] }),
    };
    const next = applyLiveEvents(prev, [
      { type: 'ExecutionStatusChanged', evt: { executionId: 'e1', workflowId: 'wf1', status: 'Cancelled', completedAt: '2026-05-05T12:03:00Z' } },
    ]);
    expect(next.e1.status).toBe('Cancelled');
    // Cancellation maps to step-level "Skipped" (no Cancelled status on steps).
    expect(next.e1.steps[0].status).toBe('Skipped');
  });

  it('ExecutionStatusChanged for unknown exec is a no-op', () => {
    const prev: LiveExecutionsById = { e1: baseExec() };
    const next = applyLiveEvents(prev, [
      { type: 'ExecutionStatusChanged', evt: { executionId: 'unknown', workflowId: 'wf1', status: 'Succeeded' } },
    ]);
    expect(next).toBe(prev);
  });
});

describe('signalrReducer.normalizeBatchItem', () => {
  it('accepts both lowercase and PascalCase keys for type/event', () => {
    const lower = normalizeBatchItem({ type: 'StepStarted', event: { executionId: 'e1', workflowId: 'wf1', stepId: 's1', stepType: 'log', startedAt: '' } });
    expect(lower?.type).toBe('StepStarted');

    const pascal = normalizeBatchItem({ Type: 'StepCompleted', Event: { executionId: 'e1', workflowId: 'wf1', stepId: 's1', status: 'Succeeded', completedAt: '' } });
    expect(pascal?.type).toBe('StepCompleted');
  });

  it('returns null when type or event is missing', () => {
    expect(normalizeBatchItem({})).toBeNull();
    expect(normalizeBatchItem({ type: 'StepStarted' })).toBeNull();
  });

  it('returns null for unknown event type', () => {
    expect(normalizeBatchItem({ type: 'NotARealType' as never, event: {} as never })).toBeNull();
  });
});
