import { describe, expect, it } from 'vitest';

import { restartServiceCommand } from './backendRestart';

describe('restartServiceCommand', () => {
  it('interpolates the service name bare inside the single-quoted inner command', () => {
    expect(restartServiceCommand('NodePilot')).toBe(
      "Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-Command','Restart-Service -Name NodePilot -Force')",
    );
  });

  it('keeps every argument of the list a balanced single-quoted string', () => {
    const command = restartServiceCommand('Node-Pilot.Api_2');
    const list = command.slice(command.indexOf('@(') + 2, -1);
    for (const argument of list.split(',')) expect(argument).toMatch(/^'[^']*'$/);
  });

  it('rejects a service name outside the validated charset', () => {
    expect(() => restartServiceCommand("Node'Pilot")).toThrow(/unsupported characters/);
    expect(() => restartServiceCommand('')).toThrow(/unsupported characters/);
  });
});
