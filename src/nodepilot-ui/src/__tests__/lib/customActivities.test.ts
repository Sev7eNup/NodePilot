import { describe, it, expect, beforeEach } from 'vitest';
import {
  isCustomActivityType,
  customActivityKeyOf,
  getCustomActivityFacts,
  getEnabledCustomActivities,
  newActivityConfig,
  useCustomActivityCatalogStore,
  type CustomActivityCatalogEntry,
} from '../../lib/customActivities';
import { describeNodeOutputs } from '../../lib/upstreamVariables';
import type { Node } from '@xyflow/react';

const entry = (over: Partial<CustomActivityCatalogEntry> = {}): CustomActivityCatalogEntry => ({
  id: 'id-1', key: 'disk_check', type: 'custom:disk_check', name: 'Disk Check', description: null,
  icon: 'extension', color: null, runsRemote: false, timeout: 'always',
  inputs: [], outputs: [{ name: 'status', type: 'string' }], isEnabled: true, version: 1, ...over,
});

describe('customActivities', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('recognizes custom types by prefix', () => {
    expect(isCustomActivityType('custom:disk_check')).toBe(true);
    expect(isCustomActivityType('runScript')).toBe(false);
    expect(isCustomActivityType('custom')).toBe(false);
    expect(customActivityKeyOf('custom:disk_check')).toBe('disk_check');
  });

  it('setCatalog syncs the synchronous module cache', () => {
    useCustomActivityCatalogStore.getState().setCatalog([entry(), entry({ id: 'id-2', key: 'b', type: 'custom:b', name: 'B', isEnabled: false })]);
    expect(getCustomActivityFacts('custom:disk_check')?.name).toBe('Disk Check');
    // getEnabledCustomActivities filters out the disabled draft.
    expect(getEnabledCustomActivities().map((c) => c.key)).toEqual(['disk_check']);
  });

  it('newActivityConfig_customType_addsDefinitionReferenceAndInputDefaults', () => {
    useCustomActivityCatalogStore.getState().setCatalog([entry({
      inputs: [
        { name: 'drive', label: 'Drive', type: 'string', default: 'C' },
        { name: 'threshold', label: 'Threshold', type: 'number', default: '90' },
        { name: 'note', label: 'Note', type: 'string' },
      ],
    })]);
    expect(newActivityConfig('custom:disk_check', { threshold: '75' })).toEqual({
      drive: 'C',
      threshold: '75',
      __customDefinitionId: 'id-1',
      __customKey: 'disk_check',
    });
  });

  it('newActivityConfig_builtInOrUnknownType_returnsBaseUnchanged', () => {
    useCustomActivityCatalogStore.getState().setCatalog([entry()]);
    expect(newActivityConfig('runScript', { script: 'x' })).toEqual({ script: 'x' });
    expect(newActivityConfig('custom:missing')).toEqual({});
  });

  it('describeNodeOutputs surfaces declared custom outputs plus exitCode', () => {
    useCustomActivityCatalogStore.getState().setCatalog([entry()]);
    const node = { id: 'step-1', data: { activityType: 'custom:disk_check', outputVariable: 'disk' } } as unknown as Node;
    const vars = describeNodeOutputs(node).map((v) => v.expression);
    expect(vars).toContain('{{disk.output}}');
    expect(vars).toContain('{{disk.param.status}}');
    expect(vars).toContain('{{disk.param.exitCode}}');
  });
});
