import { describe, it, expect } from 'vitest';
import {
  getNodeShape,
  getNodeSizeMultiplier,
  getIconScaleMultiplier,
  getBadgePositions,
  isControlFlowShape,
  SHAPE_CLIP_PATHS,
  SHAPE_DEFS,
  NODE_SHAPES,
  type NodeShape,
} from '../../components/designer/nodes/shapes';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';

/** Min/max x where the polygon silhouette crosses the horizontal mid-line (y=50%).
 *  Used to assert a shape touches the LEFT (≈0) and RIGHT (≈100) edge-midpoints, where
 *  ReactFlow anchors ports/edges. */
function spanAtMidY(clip: string): [number, number] {
  const pts = clip.replace(/^polygon\(/, '').replace(/\)$/, '').split(',')
    .map((p) => p.trim().split(/\s+/).map((n) => parseFloat(n)) as [number, number]);
  const xs: number[] = [];
  for (let i = 0; i < pts.length; i++) {
    const [x1, y1] = pts[i];
    const [x2, y2] = pts[(i + 1) % pts.length];
    if (y1 === 50) xs.push(x1);
    if ((y1 < 50 && y2 > 50) || (y1 > 50 && y2 < 50)) {
      xs.push(x1 + (x2 - x1) * (50 - y1) / (y2 - y1));
    }
  }
  return [Math.min(...xs), Math.max(...xs)];
}

/** Mirror every x-coordinate (x → 100-x) of a `polygon(...)` clip-path, leaving y untouched. */
function mirrorX(clip: string): string {
  const inner = clip.replace(/^polygon\(/, '').replace(/\)$/, '');
  const mirrored = inner.split(',').map((pair) => {
    const [x, y] = pair.trim().split(/\s+/);
    return `${100 - parseFloat(x)}% ${y}`;
  });
  return `polygon(${mirrored.join(', ')})`;
}

/** The per-activity action mapping (must mirror ACTION_SHAPE in shapes.ts). */
const ACTION_MAP: Record<string, NodeShape> = {
  runScript: 'hexPointy', fileOperation: 'plaque', folderOperation: 'trapezoidUp', fileHash: 'gem',
  zipOperation: 'chamferedSquare', serviceManagement: 'hexFlat', scheduledTask: 'pentagonUp',
  registryOperation: 'octagon', wmiQuery: 'pentagonDown', startProgram: 'blockArrow',
  powerManagement: 'power', waitForCondition: 'circle', restApi: 'chevronLeft', sql: 'cylinder',
  xmlQuery: 'kite', jsonQuery: 'trapezoidDown', emailNotification: 'banner', textFileEdit: 'house',
  generateText: 'pillH', llmQuery: 'speechBubble', log: 'shield', delay: 'stopwatch',
};

/** The per-activity control-flow mapping (must mirror CONTROL_SHAPE in shapes.ts). */
const CONTROL_MAP: Record<string, NodeShape> = {
  decision: 'diamond', junction: 'hexLong', forEach: 'reel', startWorkflow: 'tagLeft',
};

describe('node shape mapping — category shapes', () => {
  it('maps trigger activities to the left-pointing pennant', () => {
    expect(getNodeShape('manualTrigger')).toBe('pennant');
    expect(getNodeShape('scheduleTrigger')).toBe('pennant');
    expect(getNodeShape('webhookTrigger')).toBe('pennant');
  });

  it('maps returnData to the right-pointing flag (it is NOT in the control group)', () => {
    expect(getNodeShape('returnData')).toBe('flag');
    expect(isControlFlowShape('flag')).toBe(false);
  });

  it('keeps the bookend symmetry of the trigger/returnData shapes', () => {
    expect(SHAPE_CLIP_PATHS.pennant).toBe(mirrorX(SHAPE_CLIP_PATHS.flag!));
    expect(getNodeSizeMultiplier('pennant')).toBe(getNodeSizeMultiplier('flag'));
    expect(getIconScaleMultiplier('pennant')).toBe(getIconScaleMultiplier('flag'));
  });

  it('only falls back to square for unknown / unmapped activity types', () => {
    expect(getNodeShape('totallyUnknownActivity')).toBe('square');
    // every real activity now resolves to a non-square shape (see completeness test below)
    expect(getNodeShape('runScript')).not.toBe('square');
  });
});

describe('node shape mapping — per-activity action shapes', () => {
  for (const [type, shape] of Object.entries(ACTION_MAP)) {
    it(`maps ${type} → ${shape}`, () => {
      expect(getNodeShape(type)).toBe(shape);
    });
  }

  it('assigns a distinct shape to every action/logic activity (no duplicates)', () => {
    const values = Object.values(ACTION_MAP);
    expect(values).toHaveLength(22);
    expect(new Set(values).size).toBe(22);
  });
});

describe('node shape mapping — per-activity control-flow shapes', () => {
  for (const [type, shape] of Object.entries(CONTROL_MAP)) {
    it(`maps ${type} → ${shape}`, () => {
      expect(getNodeShape(type)).toBe(shape);
      expect(isControlFlowShape(shape)).toBe(true);
    });
  }

  it('keeps decision on the iconic diamond', () => {
    expect(getNodeShape('decision')).toBe('diamond');
  });

  it('marks exactly the 4 control shapes as the control-flow group', () => {
    expect(Object.values(CONTROL_MAP).every(isControlFlowShape)).toBe(true);
    // bookends + a sample action are NOT control
    for (const s of ['flag', 'pennant', 'square', 'cylinder', 'hexFlat'] as NodeShape[]) {
      expect(isControlFlowShape(s)).toBe(false);
    }
  });

  it('gives every controlFlow-category activity (except returnData) its own non-square shape', () => {
    const control = ACTIVITY_CATALOG.filter((a) => a.category === 'controlFlow' && a.type !== 'returnData');
    for (const a of control) {
      expect(getNodeShape(a.type), `${a.type} must not fall back to square`).not.toBe('square');
      expect(getNodeShape(a.type), `${a.type} mapping`).toBe(CONTROL_MAP[a.type]);
      expect(isControlFlowShape(getNodeShape(a.type))).toBe(true);
    }
    expect(control.map((a) => a.type).sort()).toEqual(Object.keys(CONTROL_MAP).sort());
  });

  it('each control shape touches the left & right edge-midpoints (clean edge routing)', () => {
    for (const shape of Object.values(CONTROL_MAP)) {
      const [minX, maxX] = spanAtMidY(SHAPE_CLIP_PATHS[shape]!);
      expect(minX, `${shape} left@mid`).toBeLessThanOrEqual(1);
      expect(maxX, `${shape} right@mid`).toBeGreaterThanOrEqual(99);
    }
  });

  it('every activity shape (action + control) is globally distinct — no shape used twice', () => {
    const all = [...Object.values(ACTION_MAP), ...Object.values(CONTROL_MAP)];
    expect(all).toHaveLength(26);
    expect(new Set(all).size).toBe(26);
    // and the shape-name union itself has no duplicate entries
    expect(new Set(NODE_SHAPES).size).toBe(NODE_SHAPES.length);
  });
});

describe('node shape registry — completeness', () => {
  it('gives every action- and logic-category activity its own non-square shape', () => {
    const shaped = ACTIVITY_CATALOG.filter((a) => a.category === 'action' || a.category === 'logic');
    for (const a of shaped) {
      const shape = getNodeShape(a.type);
      expect(shape, `${a.type} (${a.category}) must not fall back to square`).not.toBe('square');
      expect(getNodeShape(a.type), `${a.type} mapping`).toBe(ACTION_MAP[a.type]);
    }
    // catalog and test table agree on the set
    expect(shaped.map((a) => a.type).sort()).toEqual(Object.keys(ACTION_MAP).sort());
  });

  it('has a complete, well-formed registry entry for every NodeShape', () => {
    for (const s of NODE_SHAPES) {
      const def = SHAPE_DEFS[s];
      expect(def, `SHAPE_DEFS[${s}]`).toBeDefined();
      expect(def.size).toBeGreaterThan(0);
      expect(def.iconScale).toBeGreaterThan(0);
      expect(def.badges.topRight).toBeDefined();
      expect(def.badges.topLeft).toBeDefined();
      expect(def.badges.topMiddle).toBeDefined();
      expect(def.badges.bottomRight).toBeDefined();
      if (s === 'square') {
        expect(def.clip).toBeUndefined();
      } else {
        expect(def.clip, `${s} clip`).toMatch(/^polygon\(/);
      }
    }
  });

  it('derives SHAPE_CLIP_PATHS from the registry for every shape', () => {
    for (const s of NODE_SHAPES) {
      expect(SHAPE_CLIP_PATHS[s]).toBe(SHAPE_DEFS[s].clip);
    }
  });

  it('provides all four badge slots for an action shape', () => {
    const pos = getBadgePositions('cylinder');
    expect(pos.topRight).toBeDefined();
    expect(pos.topLeft).toBeDefined();
    expect(pos.topMiddle).toBeDefined();
    expect(pos.bottomRight).toBeDefined();
  });
});
