import { describe, it, expect } from 'vitest';
import {
  skippedConfigKeys,
  isRemoteActivityType,
  buildClonedDataPatch,
  applyClonedPatch,
} from '../../lib/configClone';

describe('configClone — skippedConfigKeys', () => {
  it('returns empty list for activities with no type-specific skip rules', () => {
    expect(skippedConfigKeys('runScript')).toEqual([]);
    expect(skippedConfigKeys('sql')).toEqual([]);
    expect(skippedConfigKeys('unknownActivity')).toEqual([]);
  });
});

describe('configClone — isRemoteActivityType', () => {
  it('identifies remote-capable activities', () => {
    expect(isRemoteActivityType('runScript')).toBe(true);
    expect(isRemoteActivityType('serviceManagement')).toBe(true);
    expect(isRemoteActivityType('startProgram')).toBe(true);
  });

  it('rejects engine-local activities', () => {
    expect(isRemoteActivityType('restApi')).toBe(false);
    expect(isRemoteActivityType('sql')).toBe(false);
    expect(isRemoteActivityType('delay')).toBe(false);
  });

  it('rejects triggers', () => {
    expect(isRemoteActivityType('manualTrigger')).toBe(false);
    expect(isRemoteActivityType('scheduleTrigger')).toBe(false);
  });
});

describe('configClone — buildClonedDataPatch (scope=all)', () => {
  const sourceRunScript = {
    activityType: 'runScript',
    label: 'Quelle',
    targetMachineId: 'machine-A',
    credentialId: 'cred-1',
    outputVariable: 'sourceOut',
    config: {
      script: 'Get-Date',
      engine: 'pwsh',
      timeoutSeconds: 60,
      retry: { maxAttempts: 3, backoff: 'exponential' },
    },
  };

  it('copies the entire config (incl. script body) plus machine + credential', () => {
    const patch = buildClonedDataPatch(sourceRunScript, 'runScript', 'all');
    expect(patch.targetMachineId).toBe('machine-A');
    expect(patch.credentialId).toBe('cred-1');
    expect(patch.__configPatch).toEqual({
      script: 'Get-Date',
      engine: 'pwsh',
      timeoutSeconds: 60,
      retry: { maxAttempts: 3, backoff: 'exponential' },
    });
  });

  it('does NOT copy label or outputVariable (those identify the step itself)', () => {
    const patch = buildClonedDataPatch(sourceRunScript, 'runScript', 'all');
    expect(patch).not.toHaveProperty('label');
    expect(patch).not.toHaveProperty('outputVariable');
  });

  it('copies the action payload for sql (query) and fileOperation (path)', () => {
    const sourceSql = {
      activityType: 'sql',
      config: { provider: 'postgres', connectionRef: 'reportsDb', query: 'SELECT 1', timeoutSeconds: 30 },
    };
    const sqlPatch = buildClonedDataPatch(sourceSql, 'sql', 'all');
    expect((sqlPatch.__configPatch as Record<string, unknown>).query).toBe('SELECT 1');
    expect((sqlPatch.__configPatch as Record<string, unknown>).provider).toBe('postgres');

    const sourceFile = {
      activityType: 'fileOperation',
      targetMachineId: 'm-1',
      credentialId: null,
      config: { operation: 'copy', path: 'C:\\src.txt', destination: 'C:\\dst.txt' },
    };
    const filePatch = buildClonedDataPatch(sourceFile, 'fileOperation', 'all');
    const fileCfg = filePatch.__configPatch as Record<string, unknown>;
    expect(fileCfg.path).toBe('C:\\src.txt');
    expect(fileCfg.destination).toBe('C:\\dst.txt');
    expect(fileCfg.operation).toBe('copy');
  });

  it('returns empty patch when source and target activity types differ in scope=all', () => {
    const patch = buildClonedDataPatch(sourceRunScript, 'serviceManagement', 'all');
    expect(patch).toEqual({});
  });

  it('handles missing source.config gracefully', () => {
    const minimal = {
      activityType: 'runScript',
      targetMachineId: 'machine-A',
      credentialId: null,
    };
    const patch = buildClonedDataPatch(minimal, 'runScript', 'all');
    expect(patch.targetMachineId).toBe('machine-A');
    expect(patch.credentialId).toBeNull();
    expect(patch).not.toHaveProperty('__configPatch');
  });

  it('emits a config patch even when source has only the action payload', () => {
    // Previous behaviour silently dropped the patch when "no cloneable keys" matched. New
    // behaviour: action payload is part of the clone, so a script-only source still produces
    // a patch.
    const sparse = {
      activityType: 'runScript',
      targetMachineId: 'machine-A',
      credentialId: 'cred-1',
      config: { script: 'Get-Date' },
    };
    const patch = buildClonedDataPatch(sparse, 'runScript', 'all');
    expect(patch.targetMachineId).toBe('machine-A');
    expect(patch.__configPatch).toEqual({ script: 'Get-Date' });
  });
});

describe('configClone — buildClonedDataPatch (scope=remoteOnly)', () => {
  it('copies only machine + credential, never config', () => {
    const source = {
      activityType: 'runScript',
      targetMachineId: 'machine-B',
      credentialId: 'cred-2',
      config: { script: 'whoami', timeoutSeconds: 90, retry: { maxAttempts: 2 } },
    };
    const patch = buildClonedDataPatch(source, 'serviceManagement', 'remoteOnly');
    expect(patch).toEqual({ targetMachineId: 'machine-B', credentialId: 'cred-2' });
  });

  it('returns empty patch when source is engine-local (restApi)', () => {
    const source = { activityType: 'restApi', targetMachineId: null, credentialId: null };
    const patch = buildClonedDataPatch(source, 'runScript', 'remoteOnly');
    expect(patch).toEqual({});
  });

  it('returns empty patch when target is engine-local', () => {
    const source = { activityType: 'runScript', targetMachineId: 'machine-A', credentialId: 'cred-1' };
    const patch = buildClonedDataPatch(source, 'restApi', 'remoteOnly');
    expect(patch).toEqual({});
  });
});

describe('configClone — applyClonedPatch', () => {
  it('REPLACES config (does not merge) so old fields from the target do not survive', () => {
    // Crucial: if a user clones FROM a source that has no `script` ONTO a target that has
    // `script: 'foo'`, the merge approach would keep `foo` and surprise the user. The clone
    // is "make this step look like that one" — full replacement is the right default.
    const target = {
      activityType: 'runScript',
      label: 'Ziel-Step',
      targetMachineId: null,
      credentialId: null,
      outputVariable: 'targetOut',
      config: { script: 'old-target-script', someOtherKey: 'should-not-survive' },
    };
    const patch = {
      targetMachineId: 'machine-A',
      credentialId: 'cred-1',
      __configPatch: { script: 'new-source-script', timeoutSeconds: 60, retry: { maxAttempts: 3 } },
    };
    const next = applyClonedPatch(target, patch);
    expect(next.label).toBe('Ziel-Step');
    expect(next.outputVariable).toBe('targetOut');
    expect(next.targetMachineId).toBe('machine-A');
    expect(next.credentialId).toBe('cred-1');
    expect(next.config).toEqual({
      script: 'new-source-script',
      timeoutSeconds: 60,
      retry: { maxAttempts: 3 },
    });
  });

  it('does not mutate the input target data object', () => {
    const target = {
      activityType: 'runScript',
      config: { script: 'Get-Date' },
    };
    const targetSnapshot = JSON.parse(JSON.stringify(target));
    applyClonedPatch(target, { targetMachineId: 'machine-A', __configPatch: { timeoutSeconds: 30 } });
    expect(target).toEqual(targetSnapshot);
  });

  it('handles target with no existing config object', () => {
    const target = { activityType: 'runScript' };
    const next = applyClonedPatch(target, { __configPatch: { timeoutSeconds: 30 } });
    expect(next.config).toEqual({ timeoutSeconds: 30 });
  });

  it('passes through plain (non-config) keys unchanged', () => {
    const target = { activityType: 'runScript', targetMachineId: null };
    const next = applyClonedPatch(target, { targetMachineId: 'm-1', credentialId: 'c-1' });
    expect(next.targetMachineId).toBe('m-1');
    expect(next.credentialId).toBe('c-1');
  });
});
