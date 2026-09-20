import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../api/client';
import type { StepExecution, WorkflowExecution } from '../../types/api';
import { FILE_WORKFLOW_ID } from '../../../demo/run/fileScenario';
import { configureWorld, getWorld } from '../../../demo/state/world';
import { buildWorld } from '../../../demo/seed/build';
import { installDemoBackend, uninstallDemoBackend } from '../../../demo/net/install';
import { demoRoutes } from '../../../demo/handlers';
import { setLatencyEnabled } from '../../../demo/net/latency';
import { stopAllRuns } from '../../../demo/run/player';

describe('guided file workflow through the product API', () => {
  beforeEach(() => {
    vi.useFakeTimers(); setLatencyEnabled(false);
    configureWorld(() => buildWorld()); installDemoBackend(demoRoutes);
  });
  afterEach(() => { stopAllRuns(); uninstallDemoBackend(); setLatencyEnabled(true); vi.useRealTimers(); });

  it('carries submitted inputs through the file, copy, log and return data', async () => {
    const parameters = { fileName: 'worker.json', pollIntervalSeconds: '75', environment: 'Production' };
    const run = await api.post<WorkflowExecution>(`/workflows/${FILE_WORKFLOW_ID}/execute`, { parameters });
    expect(JSON.parse(run.inputParametersJson!)).toEqual(parameters);
    await vi.runAllTimersAsync();
    const finished = await api.get<WorkflowExecution>(`/executions/${run.id}`);
    const steps = await api.get<StepExecution[]>(`/executions/${run.id}/steps`);
    expect(finished.status).toBe('Succeeded');
    expect(steps).toHaveLength(7);
    const data = JSON.parse(finished.returnData!);
    expect(JSON.parse(data.content)).toEqual({ environment: 'Production', pollIntervalSeconds: 75, service: 'NodePilot.FileWorker' });
    expect(data.destination).toBe('C:\\Apps\\FileWorker\\Production\\config\\worker.json');
    expect(data.message).toBe(steps.find(step => step.stepId === 'log')!.output);
    const workflow = getWorld().workflows.find(item => item.id === FILE_WORKFLOW_ID)!;
    expect(workflow.totalCount).toBe(getWorld().executions.filter(item => item.workflowId === FILE_WORKFLOW_ID).length);
  });

  it('stops at the denied copy and retries with the same inputs and same failure', async () => {
    const failed = getWorld().executions.find(run => run.workflowId === FILE_WORKFLOW_ID && run.status === 'Failed')!;
    expect(getWorld().steps.get(failed.id)?.map(step => step.stepId)).toEqual(['trigger', 'registry', 'service', 'powershell', 'fileCopy']);
    const retried = await api.post<WorkflowExecution>(`/executions/${failed.id}/retry`, {});
    expect(retried.inputParametersJson).toBe(failed.inputParametersJson);
    await vi.runAllTimersAsync();
    const finished = await api.get<WorkflowExecution>(`/executions/${retried.id}`);
    expect(finished.status).toBe('Failed');
    expect(finished.errorMessage).toContain('Access denied');
    expect(finished.returnData).toBeNull();
    const steps = await api.get<StepExecution[]>(`/executions/${retried.id}/steps`);
    expect(steps.at(-1)?.stepId).toBe('fileCopy');
    expect(steps.at(-1)?.outputParametersJson).toBeNull();
    expect(steps.find(step => step.stepId === 'powershell')?.output).toContain('pollIntervalSeconds');
  });

  it.each([{ fileName: '../settings.json' }, { pollIntervalSeconds: 'abc' }, { pollIntervalSeconds: '301' }, { environment: 'Unknown' }])('rejects invalid inputs before creating a run: %j', parameters => {
    const before = getWorld().executions.length;
    return expect(api.post(`/workflows/${FILE_WORKFLOW_ID}/execute`, { parameters })).rejects.toThrow().then(() => {
      expect(getWorld().executions).toHaveLength(before);
    });
  });
});
