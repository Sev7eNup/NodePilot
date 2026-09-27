import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { configureWorld, getWorld, resetWorld } from '../../../demo/state/world';
import { buildWorld } from '../../../demo/seed/build';
import { ensureMissionWorkflow } from '../../../demo/seed/missionFixtures';
import { cancelRun, startRun, stopAllRuns } from '../../../demo/run/player';

const NOW = Date.parse('2026-09-18T12:00:00Z');

describe('guided demo executions', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    configureWorld(() => buildWorld(NOW));
  });
  afterEach(() => {
    stopAllRuns();
    resetWorld();
    vi.useRealTimers();
  });

  it('takes only the critical disk-space branch at 8 GB', async () => {
    const workflow = ensureMissionWorkflow('decision')!;
    const run = startRun(workflow.id, 'manual', { freeSpaceGb: '8' })!;
    await vi.runAllTimersAsync();
    expect(run.status).toBe('Succeeded');
    expect(getWorld().steps.get(run.id)?.map(step => step.stepId)).toEqual(['trg', 'probe', 'dec', 'top_dirs', 'mail_critical', 'gather', 'ret']);
    expect(JSON.parse(run.returnData!)).toEqual({ freeSpaceGb: '8', severity: 'critical' });
  });

  it('overlaps both cleanup branches and waits for both before the email', async () => {
    const workflow = ensureMissionWorkflow('parallel')!;
    const run = startRun(workflow.id)!;
    await vi.runAllTimersAsync();
    const steps = getWorld().steps.get(run.id)!;
    const byId = (id: string) => steps.find(step => step.stepId === id)!;
    expect(run.status).toBe('Succeeded');
    expect(Date.parse(byId('checksum').startedAt)).toBeLessThan(Date.parse(byId('purge').completedAt!));
    expect(Date.parse(byId('purge').startedAt)).toBeLessThan(Date.parse(byId('checksum').completedAt!));
    expect(Date.parse(byId('gather').startedAt)).toBeGreaterThanOrEqual(Math.max(Date.parse(byId('checksum').completedAt!), Date.parse(byId('purge').completedAt!)));
    expect(Date.parse(byId('mail').startedAt)).toBeGreaterThanOrEqual(Date.parse(byId('gather').completedAt!));
  });

  it('starts and verifies a stopped service before reporting recovery', async () => {
    const workflow = ensureMissionWorkflow('service')!;
    const run = startRun(workflow.id, 'manual', { serviceState: 'Stopped' })!;
    await vi.runAllTimersAsync();
    expect(run.status).toBe('Succeeded');
    expect(getWorld().steps.get(run.id)?.map(step => step.stepId)).toEqual(['trg', 'status', 'dec', 'start', 'settle', 'verify', 'mail_recovered', 'gather', 'ret']);
    expect(JSON.parse(run.returnData!)).toEqual({ service: 'Spooler', status: 'Running' });
  });

  it('cancels a running live check without completing later activities', async () => {
    const workflow = ensureMissionWorkflow('live')!;
    const run = startRun(workflow.id)!;
    await vi.advanceTimersByTimeAsync(1200);
    expect(getWorld().steps.get(run.id)?.find(step => step.stepId === 'wait')?.status).toBe('Running');
    expect(cancelRun(run.id)).toBe(true);
    await vi.runAllTimersAsync();
    expect(run.status).toBe('Cancelled');
    expect(getWorld().steps.get(run.id)?.map(step => step.stepId)).toEqual(['trigger', 'wait']);
  });
});
