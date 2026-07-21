import { describe, it, expect, beforeEach } from 'vitest';
import {
  useDesignStore,
  NODE_SCALES,
  LABEL_FONT_OFFSETS,
  EDGE_WIDTHS,
} from '../../stores/designStore';

/**
 * The designStore drives the canvas rendering — node-style, scale, edge animation,
 * snap-to-grid, layout mode, optional overlays. It's persisted to localStorage so a
 * regression in the index-clamping or toggle logic survives the page reload that the
 * user just made. Pin every clamp + every toggle.
 */

const initialState = useDesignStore.getState();

describe('useDesignStore', () => {
  beforeEach(() => {
    // Reset to factory defaults between tests so persistence-via-localStorage doesn't
    // leak between cases.
    useDesignStore.setState(initialState, true);
  });

  it('defaults_matchClassicLayoutAndSmallestZoom', () => {
    const s = useDesignStore.getState();
    expect(s.nodeStyle).toBe('classic');
    expect(s.nodeScaleIndex).toBe(1); // sm — new default (xs rendered large graphs unreadably small)
    expect(s.edgesAnimated).toBe(true);
    expect(s.layoutMode).toBe('LR');
    expect(s.snapToGrid).toBe(false);
    expect(s.machineColoringEnabled).toBe(false);
    expect(s.failureHeatmapEnabled).toBe(false);
    expect(s.designerMode).toBe('standard');
  });

  it('designerMode_isAPersistedPresentationPreference', () => {
    useDesignStore.getState().setDesignerMode('expert');
    expect(useDesignStore.getState().designerMode).toBe('expert');
    useDesignStore.getState().setDesignerMode('standard');
    expect(useDesignStore.getState().designerMode).toBe('standard');
  });

  it('toolbarLayout_defaultsToCompact_andSetterSwitches', () => {
    expect(useDesignStore.getState().toolbarLayout).toBe('compact');
    useDesignStore.getState().setToolbarLayout('classic');
    expect(useDesignStore.getState().toolbarLayout).toBe('classic');
    useDesignStore.getState().setToolbarLayout('compact');
    expect(useDesignStore.getState().toolbarLayout).toBe('compact');
  });

  it('toolbarLayout_hydratesToCompact_whenAbsentFromAPersistedV1Profile', async () => {
    // A stored profile written before this feature has no `toolbarLayout` key. After rehydration
    // the merged state must fall back to the compact default (never `undefined`).
    localStorage.setItem('nodepilot-design', JSON.stringify({
      state: { designerMode: 'expert', nodeStyle: 'classic' },
      version: 1,
    }));
    await useDesignStore.persist.rehydrate();
    expect(useDesignStore.getState().toolbarLayout).toBe('compact');
    localStorage.removeItem('nodepilot-design');
  });

  it('designerTheme_defaultsToAtelier_andToggleAlternates', () => {
    expect(useDesignStore.getState().designerTheme).toBe('atelier');
    useDesignStore.getState().toggleDesignerTheme();
    expect(useDesignStore.getState().designerTheme).toBe('classic');
    useDesignStore.getState().toggleDesignerTheme();
    expect(useDesignStore.getState().designerTheme).toBe('atelier');
  });

  it('designerTheme_setterSetsExactValue_idempotent', () => {
    useDesignStore.getState().setDesignerTheme('classic');
    useDesignStore.getState().setDesignerTheme('classic');
    expect(useDesignStore.getState().designerTheme).toBe('classic');
    useDesignStore.getState().setDesignerTheme('atelier');
    expect(useDesignStore.getState().designerTheme).toBe('atelier');
  });

  it('designerTheme_hydratesToAtelier_whenAbsentFromAPersistedV1Profile', async () => {
    // Profiles persisted before the Atelier design carry no `designerTheme` key. After
    // rehydration the merged state must fall back to the Atelier default (never undefined).
    localStorage.setItem('nodepilot-design', JSON.stringify({
      state: { designerMode: 'expert', nodeStyle: 'classic' },
      version: 1,
    }));
    await useDesignStore.persist.rehydrate();
    expect(useDesignStore.getState().designerTheme).toBe('atelier');
    localStorage.removeItem('nodepilot-design');
  });

  it('toggleNodeIconStyle_alternatesShapeAndGlyph', () => {
    expect(useDesignStore.getState().nodeIconStyle).toBe('shape');
    useDesignStore.getState().toggleNodeIconStyle();
    expect(useDesignStore.getState().nodeIconStyle).toBe('glyph');
    useDesignStore.getState().toggleNodeIconStyle();
    expect(useDesignStore.getState().nodeIconStyle).toBe('shape');
  });

  it('toggleNodeStyle_alternatesBetweenClassicAndCard', () => {
    useDesignStore.getState().toggleNodeStyle();
    expect(useDesignStore.getState().nodeStyle).toBe('card');
    useDesignStore.getState().toggleNodeStyle();
    expect(useDesignStore.getState().nodeStyle).toBe('classic');
  });

  it('setNodeStyle_setsExactValue_idempotent', () => {
    // The segmented control sets a concrete value; re-selecting the active value is a no-op,
    // never an accidental toggle.
    useDesignStore.getState().setNodeStyle('card');
    expect(useDesignStore.getState().nodeStyle).toBe('card');
    useDesignStore.getState().setNodeStyle('card');
    expect(useDesignStore.getState().nodeStyle).toBe('card');
    useDesignStore.getState().setNodeStyle('classic');
    expect(useDesignStore.getState().nodeStyle).toBe('classic');
  });

  it('autoHidePorts_defaultsOn_andToggleAlternates', () => {
    // Default: ports hide until the cursor nears / node selected / connecting. Toggle → always show.
    expect(useDesignStore.getState().autoHidePorts).toBe(true);
    useDesignStore.getState().toggleAutoHidePorts();
    expect(useDesignStore.getState().autoHidePorts).toBe(false);
    useDesignStore.getState().toggleAutoHidePorts();
    expect(useDesignStore.getState().autoHidePorts).toBe(true);
  });

  it('nodeIconStyle_defaultsToShape_andSetterSwitches', () => {
    // Default keeps the existing shaped-silhouette look; the setter selects the bare
    // palette-glyph "icon view" (classic only) and back.
    expect(useDesignStore.getState().nodeIconStyle).toBe('shape');
    useDesignStore.getState().setNodeIconStyle('glyph');
    expect(useDesignStore.getState().nodeIconStyle).toBe('glyph');
    useDesignStore.getState().setNodeIconStyle('shape');
    expect(useDesignStore.getState().nodeIconStyle).toBe('shape');
  });

  it('zoomIn_clampedToLastScalePresetIndex', () => {
    // Don't let zoomIn run off the end of NODE_SCALES — the renderer indexes that array
    // and an out-of-bounds read would surface as undefined sizes silently.
    for (let i = 0; i < NODE_SCALES.length + 5; i++) {
      useDesignStore.getState().zoomIn();
    }
    expect(useDesignStore.getState().nodeScaleIndex).toBe(NODE_SCALES.length - 1);
  });

  it('zoomOut_clampedToZero', () => {
    useDesignStore.setState({ nodeScaleIndex: 1 });
    useDesignStore.getState().zoomOut();
    useDesignStore.getState().zoomOut();
    useDesignStore.getState().zoomOut();
    expect(useDesignStore.getState().nodeScaleIndex).toBe(0);
  });

  it('labelFontInc_clampedToLastOffsetIndex', () => {
    for (let i = 0; i < LABEL_FONT_OFFSETS.length + 3; i++) {
      useDesignStore.getState().labelFontInc();
    }
    expect(useDesignStore.getState().labelFontOffsetIndex).toBe(LABEL_FONT_OFFSETS.length - 1);
  });

  it('labelFontDec_clampedToZero', () => {
    useDesignStore.setState({ labelFontOffsetIndex: 0 });
    useDesignStore.getState().labelFontDec();
    expect(useDesignStore.getState().labelFontOffsetIndex).toBe(0);
  });

  it('edgeWidth_clampingBothSides', () => {
    for (let i = 0; i < EDGE_WIDTHS.length + 5; i++) {
      useDesignStore.getState().edgeWidthInc();
    }
    expect(useDesignStore.getState().edgeWidthIndex).toBe(EDGE_WIDTHS.length - 1);

    for (let i = 0; i < EDGE_WIDTHS.length + 5; i++) {
      useDesignStore.getState().edgeWidthDec();
    }
    expect(useDesignStore.getState().edgeWidthIndex).toBe(0);
  });

  it('toggleEdgesAnimated_flipsBoolean', () => {
    expect(useDesignStore.getState().edgesAnimated).toBe(true);
    useDesignStore.getState().toggleEdgesAnimated();
    expect(useDesignStore.getState().edgesAnimated).toBe(false);
  });

  it('setEdgeRouting_acceptsAllThreeModes', () => {
    useDesignStore.getState().setEdgeRouting('curved');
    expect(useDesignStore.getState().edgeRouting).toBe('curved');
    useDesignStore.getState().setEdgeRouting('straight');
    expect(useDesignStore.getState().edgeRouting).toBe('straight');
    useDesignStore.getState().setEdgeRouting('smart');
    expect(useDesignStore.getState().edgeRouting).toBe('smart');
  });

  it('snapToGrid_canBeToggled_andSizeBoundsArePinned', () => {
    useDesignStore.getState().setSnapToGrid(true);
    expect(useDesignStore.getState().snapToGrid).toBe(true);
    useDesignStore.getState().setSnapGridSize(40);
    expect(useDesignStore.getState().snapGridSize).toBe(40);
  });

  it('layoutMode_acceptsAllSupportedModes', () => {
    for (const m of ['LR', 'TB', 'Compact', 'ELK'] as const) {
      useDesignStore.getState().setLayoutMode(m);
      expect(useDesignStore.getState().layoutMode).toBe(m);
    }
  });

  it('machineColoring_isOptInToggle', () => {
    // Off by default — opt-in only because the per-machine stripe colors visually
    // dominate the canvas. Pin the default and the toggle.
    expect(useDesignStore.getState().machineColoringEnabled).toBe(false);
    useDesignStore.getState().toggleMachineColoring();
    expect(useDesignStore.getState().machineColoringEnabled).toBe(true);
    useDesignStore.getState().toggleMachineColoring();
    expect(useDesignStore.getState().machineColoringEnabled).toBe(false);
  });

  it('failureHeatmap_isOptInToggle', () => {
    expect(useDesignStore.getState().failureHeatmapEnabled).toBe(false);
    useDesignStore.getState().toggleFailureHeatmap();
    expect(useDesignStore.getState().failureHeatmapEnabled).toBe(true);
  });
});
