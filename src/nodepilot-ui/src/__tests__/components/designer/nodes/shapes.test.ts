import { describe, it, expect } from 'vitest';
import {
  getNodeShape,
  isControlFlowShape,
  NODE_SHAPES,
  SHAPE_DEFS,
  type NodeShape,
} from '../../../../components/designer/nodes/shapes';
import {
  ACTIVITY_CATALOG,
  TRIGGER_ACTIVITY_TYPES,
} from '../../../../lib/activityCatalog.generated';

// The four shapes that make up the control-flow group (mirror of CONTROL_SHAPE's values).
const CONTROL_SHAPES: NodeShape[] = ['diamond', 'hexLong', 'reel', 'tagLeft'];

// Expected per-activity control-flow mapping (mirror of CONTROL_SHAPE in shapes.ts).
const EXPECTED_CONTROL: Record<string, NodeShape> = {
  decision: 'diamond',
  junction: 'hexLong',
  forEach: 'reel',
  startWorkflow: 'tagLeft',
};

// Expected per-activity action mapping (mirror of ACTION_SHAPE in shapes.ts).
const EXPECTED_ACTION: Record<string, NodeShape> = {
  runScript: 'hexPointy', fileOperation: 'plaque', folderOperation: 'trapezoidUp', fileHash: 'gem',
  zipOperation: 'chamferedSquare', serviceManagement: 'hexFlat', scheduledTask: 'pentagonUp',
  registryOperation: 'octagon', wmiQuery: 'pentagonDown', startProgram: 'blockArrow',
  powerManagement: 'starburst', waitForCondition: 'circle', restApi: 'chevronLeft', sql: 'cylinder',
  xmlQuery: 'kite', jsonQuery: 'trapezoidDown', emailNotification: 'banner', textFileEdit: 'house',
  generateText: 'pillH', llmQuery: 'speechBubble', log: 'shield', delay: 'cross',
};

describe('getNodeShape — precedence', () => {
  it('resolves every trigger activity type to the pennant bookend (trigger wins first)', () => {
    expect(TRIGGER_ACTIVITY_TYPES.size).toBeGreaterThan(0);
    for (const type of TRIGGER_ACTIVITY_TYPES) {
      expect(getNodeShape(type), `${type} → pennant`).toBe('pennant');
    }
    // spot-check a couple of well-known trigger names too
    expect(getNodeShape('manualTrigger')).toBe('pennant');
    expect(getNodeShape('scheduleTrigger')).toBe('pennant');
  });

  it('maps each control-flow type to its dedicated silhouette', () => {
    expect(getNodeShape('decision')).toBe('diamond');
    expect(getNodeShape('junction')).toBe('hexLong');
    expect(getNodeShape('forEach')).toBe('reel');
    expect(getNodeShape('startWorkflow')).toBe('tagLeft');
  });

  it('maps returnData to the right-pointing flag (the other bookend, not a control shape)', () => {
    expect(getNodeShape('returnData')).toBe('flag');
    expect(isControlFlowShape('flag')).toBe(false);
  });

  it('maps each action/logic type to its ACTION_SHAPE entry', () => {
    for (const [type, shape] of Object.entries(EXPECTED_ACTION)) {
      expect(getNodeShape(type), `${type} → ${shape}`).toBe(shape);
    }
    // representative subset called out explicitly
    expect(getNodeShape('runScript')).toBe('hexPointy');
    expect(getNodeShape('sql')).toBe('cylinder');
    expect(getNodeShape('llmQuery')).toBe('speechBubble');
  });

  it('falls back to square for unknown / unmapped activity types', () => {
    expect(getNodeShape('totallyUnknownActivity')).toBe('square');
    expect(getNodeShape('')).toBe('square');
    expect(getNodeShape('custom:something')).toBe('square');
  });
});

describe('isControlFlowShape', () => {
  it('returns true for exactly the four control-flow shapes', () => {
    for (const s of CONTROL_SHAPES) {
      expect(isControlFlowShape(s), `${s} should be control`).toBe(true);
    }
  });

  it('returns false for every non-control shape (bookends, square, action shapes)', () => {
    const nonControl = NODE_SHAPES.filter((s) => !CONTROL_SHAPES.includes(s));
    for (const s of nonControl) {
      expect(isControlFlowShape(s), `${s} should not be control`).toBe(false);
    }
    // explicit callouts from the task
    for (const s of ['pennant', 'flag', 'square', 'hexPointy', 'cylinder'] as NodeShape[]) {
      expect(isControlFlowShape(s), s).toBe(false);
    }
  });
});

describe('shape registry — consistency', () => {
  it('has a SHAPE_DEFS definition for every NODE_SHAPES entry (and vice versa)', () => {
    for (const s of NODE_SHAPES) {
      expect(SHAPE_DEFS[s], `SHAPE_DEFS[${s}]`).toBeDefined();
    }
    expect(Object.keys(SHAPE_DEFS).sort()).toEqual([...NODE_SHAPES].sort());
  });

  it('getNodeShape returns a registered NodeShape for every catalog activity type', () => {
    const registered = new Set<string>(NODE_SHAPES);
    const types = [...ACTIVITY_CATALOG.map((a) => a.type), 'unknownFutureActivity'];
    for (const type of types) {
      const shape = getNodeShape(type);
      expect(registered.has(shape), `${type} → ${shape} not in NODE_SHAPES`).toBe(true);
      expect(SHAPE_DEFS[shape], `SHAPE_DEFS[${shape}] (from ${type})`).toBeDefined();
    }
  });

  it('every catalog action/logic activity has a distinct non-square shape matching ACTION_SHAPE', () => {
    const shaped = ACTIVITY_CATALOG.filter((a) => a.category === 'action' || a.category === 'logic');
    for (const a of shaped) {
      expect(getNodeShape(a.type), `${a.type} must not be square`).not.toBe('square');
      expect(getNodeShape(a.type), `${a.type} mapping`).toBe(EXPECTED_ACTION[a.type]);
    }
    // catalog set and the expected table agree
    expect(shaped.map((a) => a.type).sort()).toEqual(Object.keys(EXPECTED_ACTION).sort());
  });

  it('every catalog control-flow activity (except returnData) maps to a control shape', () => {
    const control = ACTIVITY_CATALOG.filter((a) => a.category === 'controlFlow' && a.type !== 'returnData');
    for (const a of control) {
      const shape = getNodeShape(a.type);
      expect(shape, `${a.type} mapping`).toBe(EXPECTED_CONTROL[a.type]);
      expect(isControlFlowShape(shape), `${a.type} is control`).toBe(true);
    }
    expect(control.map((a) => a.type).sort()).toEqual(Object.keys(EXPECTED_CONTROL).sort());
  });
});
