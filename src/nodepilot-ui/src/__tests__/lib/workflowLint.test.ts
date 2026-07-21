import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { lintWorkflow } from '../../lib/workflowLint';

// Helper to build an activity node with explicit position + measured size.
function node(id: string, x: number, y: number, opts: Partial<{ w: number; h: number; activityType: string; label: string; config: Record<string, unknown> }> = {}): Node {
  return {
    id,
    type: 'activity',
    position: { x, y },
    measured: { width: opts.w ?? 140, height: opts.h ?? 132 },
    data: {
      activityType: opts.activityType ?? 'runScript',
      label: opts.label ?? id,
      config: opts.config ?? { script: 'Get-Date' },
    },
  } as unknown as Node;
}

function edge(id: string, source: string, target: string): Edge {
  return { id, source, target, type: 'labeled', data: {} };
}

describe('lintWorkflow — edge-occluded', () => {
  it('flags an edge whose straight path cuts through another node', () => {
    // Layout that reproduces the user-reported bug: one source fanning out to 3 targets stacked
    // vertically on the same x-column. The edge from `src` to the bottom target runs straight
    // through the middle target's bounding box.
    const nodes: Node[] = [
      node('src', 0, 500, { label: 'Source' }),              // source on the left
      node('mid', 400, 500, { label: 'Middle target' }),      // directly east of src, same y
      node('bottom', 400, 1000, { label: 'Bottom target' }),  // east of src, way below
    ];
    const edges: Edge[] = [
      edge('e1', 'src', 'mid'),      // straight horizontal — clean
      edge('e2', 'src', 'bottom'),   // diagonal, brushes past mid
    ];
    const { warnings } = lintWorkflow(nodes, edges);
    const occluded = warnings.filter((w) => w.code === 'edge-occluded');
    expect(occluded).toHaveLength(0); // this configuration should be fine — bottom is far enough below
  });

  it('flags an edge that runs through a node stacked between source and target', () => {
    // Source at (0, 0), target at (400, 800). A middle node sits at (200, 400) — dead-center
    // on the straight line from source-right-handle to target-left-handle.
    const nodes: Node[] = [
      node('src', 0, 0, { label: 'Source' }),
      node('blocker', 200, 400, { label: 'Blocker' }),
      node('tgt', 400, 800, { label: 'Target' }),
    ];
    const edges: Edge[] = [
      edge('e1', 'src', 'tgt'),
    ];
    const { warnings } = lintWorkflow(nodes, edges);
    const occluded = warnings.filter((w) => w.code === 'edge-occluded');
    expect(occluded).toHaveLength(1);
    expect(occluded[0].edgeId).toBe('e1');
    expect(occluded[0].message).toContain('Blocker');
    expect(occluded[0].message.toLowerCase()).toContain('auto-layout');
  });

  it('names multiple blockers when an edge passes through several nodes', () => {
    // Two blockers stacked on the diagonal from src to tgt.
    const nodes: Node[] = [
      node('src', 0, 0, { label: 'Source' }),
      node('b1', 150, 300, { label: 'First blocker' }),
      node('b2', 300, 600, { label: 'Second blocker' }),
      node('tgt', 450, 900, { label: 'Target' }),
    ];
    const edges: Edge[] = [edge('e1', 'src', 'tgt')];
    const { warnings } = lintWorkflow(nodes, edges);
    const occluded = warnings.filter((w) => w.code === 'edge-occluded');
    expect(occluded).toHaveLength(1);
    expect(occluded[0].message).toContain('2 Nodes');
    expect(occluded[0].message).toContain('First blocker');
  });

  it('does not flag edges that route cleanly between well-separated nodes', () => {
    const nodes: Node[] = [
      node('a', 0, 0),
      node('b', 400, 0),
      node('c', 800, 0),
    ];
    const edges: Edge[] = [edge('a-b', 'a', 'b'), edge('b-c', 'b', 'c')];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.filter((w) => w.code === 'edge-occluded')).toHaveLength(0);
  });

  it('does not flag edges that barely graze a neighbor (10px margin)', () => {
    // Source at (0, 0), target at (400, 0) on same row. Node just above at y=-100 sits above
    // the straight line y=0 — no collision.
    const nodes: Node[] = [
      node('src', 0, 0),
      node('above', 150, -200),
      node('tgt', 400, 0),
    ];
    const edges: Edge[] = [edge('e1', 'src', 'tgt')];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.filter((w) => w.code === 'edge-occluded')).toHaveLength(0);
  });

  it('skips disabled edges', () => {
    const nodes: Node[] = [
      node('src', 0, 0),
      node('blocker', 200, 400),
      node('tgt', 400, 800),
    ];
    const edges: Edge[] = [
      { ...edge('e1', 'src', 'tgt'), data: { disabled: true } },
    ];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.filter((w) => w.code === 'edge-occluded')).toHaveLength(0);
  });

  it('detects explicit Bottom->Top port occlusion through a middle node', () => {
    // Fan-out from `run-script` to vertically stacked targets. A bottom->top edge to the
    // bottom-most target swings through the column of middle targets, so lint should flag
    // it when the saved edge ports actually use that route.
    const nodes: Node[] = [
      node('src',    850, 628, { w: 90, h: 99, label: 'Source' }),
      node('mid1', 1120, 717, { w: 90, h: 86, label: 'Middle 1' }),
      node('mid2', 1120, 883, { w: 90, h: 99, label: 'Middle 2' }),
      node('mid3', 1120, 1062, { w: 90, h: 86, label: 'Middle 3' }),
      node('bot',  1120, 1228, { w: 90, h: 86, label: 'Bottom' }),
    ];
    const edges: Edge[] = [{ ...edge('e-src-bot', 'src', 'bot'), sourceHandle: 'bottom', targetHandle: 'top' }];
    const result = lintWorkflow(nodes, edges);
    const occluded = result.warnings.filter((w) => w.code === 'edge-occluded');
    expect(occluded).toHaveLength(1);
    expect(occluded[0].edgeId).toBe('e-src-bot');
    // At least one of the three middle nodes should be reported as occluded.
    expect(occluded[0].message).toMatch(/Middle/);
  });

  it('tolerates nodes with no measured dimensions (falls back to default size)', () => {
    const bare = (id: string, x: number, y: number): Node => ({
      id,
      type: 'activity',
      position: { x, y },
      data: { activityType: 'runScript', label: id, config: { script: 'x' } },
    } as unknown as Node);

    const nodes: Node[] = [bare('src', 0, 0), bare('blocker', 200, 400), bare('tgt', 400, 800)];
    const edges: Edge[] = [edge('e1', 'src', 'tgt')];
    const result = lintWorkflow(nodes, edges);
    // Default 140x132 — with shrink=10 still big enough to be hit by the diagonal.
    expect(result.warnings.filter((w) => w.code === 'edge-occluded')).toHaveLength(1);
  });
});

describe('lintWorkflow — duplicate-edge', () => {
  it('flags duplicate edges with the same source and target even when ports differ', () => {
    const nodes: Node[] = [node('a', 0, 0), node('b', 240, 0)];
    const edges: Edge[] = [
      { ...edge('e1', 'a', 'b'), sourceHandle: 'right', targetHandle: 'left' },
      { ...edge('e2', 'a', 'b'), sourceHandle: 'bottom', targetHandle: 'top' },
    ];

    const result = lintWorkflow(nodes, edges);
    const duplicate = result.errors.filter((e) => e.code === 'duplicate-edge');
    expect(duplicate).toHaveLength(1);
    expect(duplicate[0].edgeId).toBe('e2');
  });

  it('allows different source nodes to connect into the same target', () => {
    const nodes: Node[] = [node('a', 0, 0), node('b', 0, 180), node('target', 240, 80)];
    const edges: Edge[] = [
      edge('e1', 'a', 'target'),
      edge('e2', 'b', 'target'),
    ];

    const result = lintWorkflow(nodes, edges);
    expect(result.errors.filter((e) => e.code === 'duplicate-edge')).toHaveLength(0);
  });
});

describe('lintWorkflow — orphan-root', () => {
  it('warns when a non-trigger node has no incoming edge in a trigger-based workflow', () => {
    // Scenario: user deleted the edge feeding "orphan". Graph still has a trigger, so engine
    // will only start from the trigger — orphan and its chain become unreachable at runtime.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger', label: 'Start' }),
      node('a', 200, 0, { label: 'Reachable' }),
      node('orphan', 0, 300, { label: 'Orphan' }),
      node('tail', 200, 300, { label: 'Tail' }),
    ];
    const edges: Edge[] = [
      edge('e1', 'trig', 'a'),
      edge('e2', 'orphan', 'tail'),
    ];
    const { warnings } = lintWorkflow(nodes, edges);
    const orphanWarnings = warnings.filter((w) => w.code === 'orphan-root');
    expect(orphanWarnings).toHaveLength(1);
    expect(orphanWarnings[0].nodeId).toBe('orphan');
    expect(orphanWarnings[0].message).toContain('Orphan');
  });

  it('emits a single no-trigger error (not orphan-root/unreachable) for a trigger-less workflow', () => {
    // No trigger anywhere → roots are trigger-only, so the engine runs nothing. The lint surfaces
    // ONE clear `no-trigger` error and does NOT spam orphan-root/unreachable per node.
    const nodes: Node[] = [node('step-1', 0, 0), node('step-2', 200, 0)];
    const edges: Edge[] = [edge('e1', 'step-1', 'step-2')];
    const { errors, warnings } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(1);
    expect(warnings.filter((w) => w.code === 'orphan-root')).toHaveLength(0);
    expect(warnings.filter((w) => w.code === 'unreachable-node')).toHaveLength(0);
  });

  it('does not warn for trigger nodes themselves (they are legitimate roots)', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger', label: 'T' }),
      node('a', 200, 0, { label: 'A' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'a')];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.filter((w) => w.code === 'orphan-root')).toHaveLength(0);
  });
});

describe('lintWorkflow — no-trigger', () => {
  it('errors when there is an activity but no trigger', () => {
    const nodes: Node[] = [node('a', 0, 0), node('b', 200, 0)];
    const edges: Edge[] = [edge('e1', 'a', 'b')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(1);
  });

  it('does not error when a manualTrigger is present', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('a', 200, 0),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'a')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(0);
  });

  it('does not error when a non-manual (schedule) trigger is present', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'scheduleTrigger', config: { cronExpression: '0 0 * * * ?' } }),
      node('a', 200, 0),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'a')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(0);
  });

  it('errors when the ONLY trigger is disabled (a disabled trigger is no entry point)', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('a', 200, 0),
    ];
    // Mark the trigger disabled — it must not count as a usable root.
    (nodes[0].data as Record<string, unknown>).disabled = true;
    const edges: Edge[] = [edge('e1', 'trig', 'a')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(1);
  });

  it('does not error for an empty graph', () => {
    const { errors } = lintWorkflow([], []);
    expect(errors.filter((e) => e.code === 'no-trigger')).toHaveLength(0);
  });
});

describe('lintWorkflow — template runtime namespaces', () => {
  it('allows globals and manual runtime namespaces', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('use', 200, 0, {
        activityType: 'log',
        config: { message: '{{globals.ENV}} {{manual.user}}' },
      }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'use')];

    const { warnings } = lintWorkflow(nodes, edges);

    expect(warnings.filter((w) => w.code === 'unknown-template-ref')).toHaveLength(0);
  });

  it('flags trigger and webhook pseudo namespaces as unknown template references', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('use-trigger', 200, 0, {
        activityType: 'log',
        config: { message: '{{trigger.filePath}}' },
      }),
      node('use-webhook', 400, 0, {
        activityType: 'log',
        config: { message: '{{webhook.body}}' },
      }),
    ];
    const edges: Edge[] = [
      edge('e1', 'trig', 'use-trigger'),
      edge('e2', 'use-trigger', 'use-webhook'),
    ];

    const { warnings } = lintWorkflow(nodes, edges);

    expect(warnings.filter((w) => w.code === 'unknown-template-ref')).toHaveLength(2);
    expect(warnings.some((w) => w.nodeId === 'use-trigger')).toBe(true);
    expect(warnings.some((w) => w.nodeId === 'use-webhook')).toBe(true);
  });
});

describe('lintWorkflow - runScript execution target', () => {
  it('does not require a target machine because runScript can run locally', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('script', 250, 0, { activityType: 'runScript' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'script')];

    const { errors } = lintWorkflow(nodes, edges);

    expect(errors.some((e) => e.code === 'missing-target-machine' && e.nodeId === 'script')).toBe(false);
  });

  it('does not require a target machine for waitForCondition (hybrid like runScript)', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('wait', 250, 0, { activityType: 'waitForCondition', config: { script: '$true' } }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'wait')];

    const { errors } = lintWorkflow(nodes, edges);

    expect(errors.some((e) => e.code === 'missing-target-machine' && e.nodeId === 'wait')).toBe(false);
  });
});

describe('lintWorkflow — startjob-in-runspace', () => {
  // Pinned behavior: for engine: "auto" or "runspace", the lint rule must warn on scripts
  // using Get-WindowsUpdateLog / Start-Job / Invoke-Command -AsJob, because the in-process
  // runspace has no co-located pwsh.exe it can spawn a child process from. For
  // engine: "pwsh" / "powershell" that happens in an external process — no warning needed.

  it('warns when Get-WindowsUpdateLog runs in engine: auto', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        label: 'Generate WindowsUpdate.log',
        config: { engine: 'auto', script: 'Get-WindowsUpdateLog -LogPath $p -EA SilentlyContinue' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    const hit = warnings.filter((w) => w.code === 'startjob-in-runspace');
    expect(hit).toHaveLength(1);
    expect(hit[0].nodeId).toBe('s');
    expect(hit[0].message).toContain('Get-WindowsUpdateLog');
    expect(hit[0].message).toContain('engine: "pwsh"');
  });

  it('warns on direct Start-Job in engine: runspace', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { engine: 'runspace', script: 'Start-Job -ScriptBlock { 1..3 } | Wait-Job' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(true);
  });

  it('warns on Invoke-Command -AsJob in engine: auto', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { engine: 'auto', script: 'Invoke-Command -ComputerName HOST1 -ScriptBlock { Get-Date } -AsJob' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(true);
  });

  it('does not warn when engine: pwsh — external process can spawn child pwsh', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { engine: 'pwsh', script: 'Get-WindowsUpdateLog -LogPath $p' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(false);
  });

  it('treats missing engine as "auto" (default)', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { script: 'Get-WindowsUpdateLog -LogPath $p' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(true);
  });

  it('does not false-positive on Stop-Job / Wait-Job standalone (operate on existing jobs, no spawn)', () => {
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { engine: 'auto', script: 'Get-Job | Wait-Job; Get-Job | Stop-Job' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(false);
  });

  it('does not match Start-Job substring inside a longer identifier', () => {
    // "$myStart-Job" or "Foo-Start-Job" should not trip the warning — only the actual cmdlet.
    const nodes: Node[] = [
      node('s', 0, 0, {
        activityType: 'runScript',
        config: { engine: 'auto', script: 'Write-Output "fake-Start-Job in string"' },
      }),
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(false);
  });

  it('skips disabled nodes (no lint noise for paused steps)', () => {
    const nodes: Node[] = [
      {
        id: 's',
        type: 'activity',
        position: { x: 0, y: 0 },
        measured: { width: 140, height: 132 },
        data: {
          activityType: 'runScript',
          label: 'disabled step',
          disabled: true,
          config: { engine: 'auto', script: 'Get-WindowsUpdateLog' },
        },
      } as unknown as Node,
    ];
    const { warnings } = lintWorkflow(nodes, []);
    expect(warnings.some((w) => w.code === 'startjob-in-runspace')).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// File / Folder operation lint coverage
// ---------------------------------------------------------------------------

function fsNode(id: string, activityType: string, config: Record<string, unknown>, machineId: string | null = 'm-1'): Node {
  // machineId === null -> intentionally omit targetMachineId (used by missing-target-machine tests)
  const data: Record<string, unknown> = { activityType, label: id, config };
  if (machineId !== null) data.targetMachineId = machineId;
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    measured: { width: 140, height: 132 },
    data,
  } as unknown as Node;
}

describe('lintWorkflow — fileOperation required-config', () => {
  it('flags missing path', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'delete' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    const missing = errors.filter((e) => e.code === 'missing-required-config' && e.nodeId === 'op');
    expect(missing).toHaveLength(1);
    expect(missing[0].message).toContain('Pfad');
  });

  it('flags copy without destination', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'copy', path: 'C:\\f.txt' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.message.includes('destination'))).toBe(true);
  });

  it('flags rename without newName', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'rename', path: 'C:\\old.txt' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.message.includes('newName'))).toBe(true);
  });

  it('flags folder-only operations as unknown', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'list', path: 'C:\\dir' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    const unknown = errors.filter((e) => e.code === 'missing-required-config' && e.message.toLowerCase().includes('unbekannte'));
    expect(unknown).toHaveLength(1);
    expect(unknown[0].message).toContain('list');
  });

  it('accepts every documented file op including create', () => {
    // Pin the full file-op set: copy/move/delete/exists/create/rename. If a refactor
    // drops one (e.g. the newly-added create), this loop catches it before the UI
    // silently rejects valid workflows.
    for (const op of ['copy', 'move', 'delete', 'exists', 'create', 'rename']) {
      const nodes: Node[] = [
        fsNode('trig', 'manualTrigger', {}),
        fsNode('op', 'fileOperation', {
          operation: op,
          path: 'C:\\f.txt',
          ...(op === 'copy' || op === 'move' ? { destination: 'D:\\f.txt' } : {}),
          ...(op === 'rename' ? { newName: 'g.txt' } : {}),
        }),
      ];
      const edges: Edge[] = [edge('e1', 'trig', 'op')];
      const { errors } = lintWorkflow(nodes, edges);
      const unknownErrs = errors.filter(
        (e) => e.code === 'missing-required-config' && e.message.toLowerCase().includes('unbekannte'),
      );
      expect(unknownErrs, `op=${op} should not be flagged unknown`).toHaveLength(0);
    }
  });

  it('create op needs only path (no destination, no newName)', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'create', path: 'C:\\new.txt' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'missing-required-config')).toHaveLength(0);
  });

  it('valid copy passes lint', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'copy', path: 'C:\\f.txt', destination: 'D:\\f.txt' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.filter((e) => e.code === 'missing-required-config')).toHaveLength(0);
  });

  it('flags missing target machine on remote fileOperation', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'fileOperation', { operation: 'delete', path: 'C:\\f.txt' }, null),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'missing-target-machine' && e.nodeId === 'op')).toBe(true);
  });
});

describe('lintWorkflow — folderOperation required-config', () => {
  it('flags missing path', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'folderOperation', { operation: 'list' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'missing-required-config' && e.message.includes('Pfad'))).toBe(true);
  });

  it('flags copy without destination', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'folderOperation', { operation: 'copy', path: 'C:\\src' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.message.includes('destination'))).toBe(true);
  });

  it('flags rename without newName', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'folderOperation', { operation: 'rename', path: 'C:\\old' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.message.includes('newName'))).toBe(true);
  });

  it('accepts every documented folder op', () => {
    // Pin the full op-set: if a refactor accidentally drops one of the seven, this check
    // breaks instead of the UI silently rejecting workflows that were previously valid.
    for (const op of ['copy', 'move', 'delete', 'exists', 'list', 'create', 'rename']) {
      const nodes: Node[] = [
        fsNode('trig', 'manualTrigger', {}),
        fsNode('op', 'folderOperation', {
          operation: op,
          path: 'C:\\dir',
          ...(op === 'copy' || op === 'move' ? { destination: 'D:\\dst' } : {}),
          ...(op === 'rename' ? { newName: 'new' } : {}),
        }),
      ];
      const edges: Edge[] = [edge('e1', 'trig', 'op')];
      const { errors } = lintWorkflow(nodes, edges);
      const unknownErrs = errors.filter(
        (e) => e.code === 'missing-required-config' && e.message.toLowerCase().includes('unbekannte'),
      );
      expect(unknownErrs, `op=${op} should not be flagged unknown`).toHaveLength(0);
    }
  });

  it('flags an unknown folder op', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'folderOperation', { operation: 'purge', path: 'C:\\dir' }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    const unknown = errors.filter((e) => e.code === 'missing-required-config' && e.message.toLowerCase().includes('unbekannte'));
    expect(unknown).toHaveLength(1);
    expect(unknown[0].message).toContain('purge');
  });

  it('flags missing target machine on remote folderOperation', () => {
    const nodes: Node[] = [
      fsNode('trig', 'manualTrigger', {}),
      fsNode('op', 'folderOperation', { operation: 'list', path: 'C:\\dir' }, null),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'op')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'missing-target-machine' && e.nodeId === 'op')).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// Disabled-node handling — a disabled node must never be reported as an error,
// at most as a warning (hint: anything downstream gets cascade-skipped).
// ---------------------------------------------------------------------------

function disabledNode(id: string, x: number, y: number, opts: { activityType?: string; outputVariable?: string; config?: Record<string, unknown> } = {}): Node {
  return {
    id, type: 'activity', position: { x, y },
    measured: { width: 140, height: 132 },
    data: {
      activityType: opts.activityType ?? 'runScript',
      label: id,
      disabled: true,
      ...(opts.outputVariable !== undefined ? { outputVariable: opts.outputVariable } : {}),
      config: opts.config ?? { script: 'Get-Date' },
    },
  } as unknown as Node;
}

describe('lintWorkflow — disabled node tolerance', () => {
  it('does not raise isolated-node error for a disabled orphan', () => {
    // A disabled step with no edges is legitimate — the author switched it off instead
    // of deleting it. This must not block publishing.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('a', 200, 0),
      disabledNode('off', 0, 400),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'a')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'isolated-node' && e.nodeId === 'off')).toBe(false);
  });

  it('does not raise dup-output-variable error when only the disabled clone duplicates', () => {
    // A disabled step can't emit output — its outputVariable doesn't count as a collision.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('live', 200, 0),
      disabledNode('dead', 200, 200, { outputVariable: 'live' }),
    ];
    // "live" uses its node id as the implicit variable name; "dead" explicitly sets
    // outputVariable to 'live'.
    const edges: Edge[] = [edge('e1', 'trig', 'live'), edge('e2', 'trig', 'dead')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'dup-output-variable')).toBe(false);
  });

  it('does not raise missing-required-config error for a disabled node', () => {
    // A fileOperation without a path would fire an error if it were live, but since
    // it's disabled the lint must let it pass.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      disabledNode('off', 200, 0, { activityType: 'fileOperation', config: {} }),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'off')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'missing-required-config' && e.nodeId === 'off')).toBe(false);
  });

  it('warns disabled-with-downstream when disabled node still has outgoing edges', () => {
    // Common case: the author disables a step in the middle of the workflow. The engine
    // cascade-skips everything after it — that should surface as a warning, but must
    // NOT block publishing.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      disabledNode('off', 200, 0),
      node('downstream', 400, 0),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'off'), edge('e2', 'off', 'downstream')];
    const { errors, warnings } = lintWorkflow(nodes, edges);
    expect(errors).toHaveLength(0);
    const w = warnings.filter((w) => w.code === 'disabled-with-downstream');
    expect(w).toHaveLength(1);
    expect(w[0].nodeId).toBe('off');
  });

  it('emits disabled-with-downstream only once per disabled node even with multiple outgoing edges', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      disabledNode('off', 200, 0),
      node('a', 400, 0),
      node('b', 400, 200),
    ];
    const edges: Edge[] = [
      edge('e1', 'trig', 'off'),
      edge('e2', 'off', 'a'),
      edge('e3', 'off', 'b'),
    ];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.filter((w) => w.code === 'disabled-with-downstream')).toHaveLength(1);
  });

  it('does not warn disabled-with-downstream when disabled node is a dead-end', () => {
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      disabledNode('off', 200, 0),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'off')];
    const { warnings } = lintWorkflow(nodes, edges);
    expect(warnings.some((w) => w.code === 'disabled-with-downstream')).toBe(false);
  });

  it('does not raise isolated-node error for a node reachable only via a disabled edge', () => {
    // Scenario from the styleguide demo: a `disabled_log` node used as an edge-demo target.
    // The node itself isn't disabled, but its only incoming edge has data.disabled:true.
    // This used to be flagged as an isolated-node error (because the active-edges filter
    // drops disabled edges) — that blocked publishing and was semantically wrong: a
    // disabled edge is a deliberate design choice, not a forgotten connection.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('main', 200, 0),
      node('deadend', 200, 400, { label: 'DISABLED edge target' }),
    ];
    const edges: Edge[] = [
      edge('e1', 'trig', 'main'),
      // disabled edge leading to the dead-end node
      { id: 'e2', source: 'main', target: 'deadend', type: 'labeled', data: { disabled: true } } as Edge,
    ];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'isolated-node' && e.nodeId === 'deadend')).toBe(false);
  });

  it('still raises isolated-node error for a truly orphaned node (no edges at all)', () => {
    // Regression check: the loosened rule above should only cover the disabled-edge case,
    // not wave through every orphan node in general.
    const nodes: Node[] = [
      node('trig', 0, 0, { activityType: 'manualTrigger' }),
      node('main', 200, 0),
      node('truly-orphan', 0, 400),
    ];
    const edges: Edge[] = [edge('e1', 'trig', 'main')];
    const { errors } = lintWorkflow(nodes, edges);
    expect(errors.some((e) => e.code === 'isolated-node' && e.nodeId === 'truly-orphan')).toBe(true);
  });
});
