import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { configureWorld, getWorld, notifyWorld, resetWorld } from '../../../demo/state/world';
import { buildWorld } from '../../../demo/seed/build';
import { ensureMissionWorkflow } from '../../../demo/seed/missionFixtures';
import { cancelRun, startRun, stopAllRuns } from '../../../demo/run/player';
import { createMemoryRouter } from 'react-router';
import { mountAdditionalTours } from '../../../demo/ui/additionalTours';

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
    expect(Date.parse(byId('checksum').startedAt!)).toBeLessThan(Date.parse(byId('purge').completedAt!));
    expect(Date.parse(byId('purge').startedAt!)).toBeLessThan(Date.parse(byId('checksum').completedAt!));
    expect(Date.parse(byId('gather').startedAt!)).toBeGreaterThanOrEqual(Math.max(Date.parse(byId('checksum').completedAt!), Date.parse(byId('purge').completedAt!)));
    expect(Date.parse(byId('mail').startedAt!)).toBeGreaterThanOrEqual(Date.parse(byId('gather').completedAt!));
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
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(run.status).toBe('Running');
    expect(getWorld().steps.get(run.id)?.find(step => step.stepId === 'wait')?.status).toBe('Running');
    expect(cancelRun(run.id)).toBe(true);
    await vi.runAllTimersAsync();
    expect(run.status).toBe('Cancelled');
    expect(getWorld().steps.get(run.id)?.map(step => step.stepId)).toEqual(['trigger', 'wait']);
  });

  it('keeps the Live Ops button mounted during unrelated world updates', async () => {
    document.body.innerHTML = '<div id="root"></div>';
    const router = createMemoryRouter([{ path: '*', element: null }]);
    const tour = mountAdditionalTours(router);
    try {
      tour.start('live');
      const workflow = ensureMissionWorkflow('live')!;
      startRun(workflow.id);
      await vi.advanceTimersByTimeAsync(2000);
      const button = document.querySelector('[data-tour-action="open-ops"]') as HTMLButtonElement;
      expect(button).not.toBeNull();
      button.focus();
      notifyWorld();
      notifyWorld();
      expect(document.querySelector('[data-tour-action="open-ops"]')).toBe(button);
      expect(document.activeElement).toBe(button);
      button.click();
      expect(router.state.location.pathname).toBe('/operations');
    } finally {
      tour.dispose();
      router.dispose();
      document.body.innerHTML = '';
    }
  });

  it('localizes guided notes without claiming the manual service task is scheduled', () => {
    const workflow = ensureMissionWorkflow('service', 'de')!;
    const notes = JSON.parse(workflow.definitionJson).nodes.filter((node: { type: string }) => node.type === 'stickyNote');
    expect(notes.map((node: { data: { text: string } }) => node.data.text).join('\n')).toContain('Diese Übung hat keinen Zeitplan');
    expect(workflow.definitionJson).not.toContain('every 15 minutes');
  });
});
