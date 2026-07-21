import { create } from 'zustand';
import { persist } from 'zustand/middleware';

/** Which visual language the workflow designer renders in. `atelier` = the skin-independent
 *  "Atelier" redesign (light-first workbench: floating chrome, paper/graphite canvas, cobalt
 *  accent); `classic` = the previous look including app-skin passthrough. Evaluation toggle —
 *  both looks share the exact same components and functionality. */
export type DesignerTheme = 'atelier' | 'classic';
export type NodeStyle = 'classic' | 'card';
/** Classic-only: how an activity node draws its icon. `shape` = clip-path silhouette + small
 *  centered glyph (the default look); `glyph` = the bare palette icon (large, accent-coloured,
 *  no silhouette) — the same icon shown in the left "Actions" palette. Ignored in card mode. */
export type NodeIconStyle = 'shape' | 'glyph';
export type EdgeRouting = 'smart' | 'curved' | 'straight';
export type DesignerMode = 'standard' | 'expert';
/** Editor-header toolbar arrangement. `compact` = the grouped three-zone toolbar (default);
 *  `classic` = the pre-redesign row where every control is its own inline button. */
export type ToolbarLayout = 'compact' | 'classic';

export type LayoutMode = 'LR' | 'TB' | 'Compact' | 'ELK';
export const LAYOUT_MODES: LayoutMode[] = ['LR', 'TB', 'Compact', 'ELK'];
export const LAYOUT_LABELS: Record<LayoutMode, string> = { LR: 'LR', TB: 'TB', Compact: 'Cmpct', ELK: 'ELK' };

// Scale presets: everything that scales with node size (edge labels + tooltips too)
export const NODE_SCALES = [
  { key: 'xs', iconBox: 24, iconFont: 13, labelFont: 12, labelWidth: 68,  handleSize: 5, edgeLabelFont: 9,  tooltipFont: 10, tooltipMaxWidth: 200 },
  { key: 'sm', iconBox: 32, iconFont: 17, labelFont: 13, labelWidth: 80,  handleSize: 6, edgeLabelFont: 10, tooltipFont: 11, tooltipMaxWidth: 220 },
  { key: 'md', iconBox: 44, iconFont: 22, labelFont: 14, labelWidth: 96,  handleSize: 7, edgeLabelFont: 11, tooltipFont: 12, tooltipMaxWidth: 240 },
  { key: 'lg', iconBox: 56, iconFont: 28, labelFont: 15, labelWidth: 108, handleSize: 8, edgeLabelFont: 12, tooltipFont: 13, tooltipMaxWidth: 260 },
  { key: 'xl', iconBox: 72, iconFont: 36, labelFont: 17, labelWidth: 128, handleSize: 9, edgeLabelFont: 13, tooltipFont: 14, tooltipMaxWidth: 300 },
  { key: 'xxl',iconBox: 92, iconFont: 46, labelFont: 19, labelWidth: 160, handleSize: 10, edgeLabelFont: 14, tooltipFont: 15, tooltipMaxWidth: 340 },
  { key: '3xl',iconBox: 116, iconFont: 58, labelFont: 21, labelWidth: 190, handleSize: 11, edgeLabelFont: 15, tooltipFont: 16, tooltipMaxWidth: 380 },
  { key: '4xl',iconBox: 148, iconFont: 74, labelFont: 24, labelWidth: 230, handleSize: 12, edgeLabelFont: 16, tooltipFont: 17, tooltipMaxWidth: 420 },
] as const;

// Edge stroke-width presets (px). Applies to both animated and static edges.
export const EDGE_WIDTHS = [1.5, 2, 2.5, 3.5, 5, 7] as const;

// Node-label font-size offsets (px) added on top of NODE_SCALES[i].labelFont.
// Lets the author fine-tune readability without changing the whole icon-size step.
export const LABEL_FONT_OFFSETS = [-4, -2, 0, 2, 4, 6, 8] as const;

export const SNAP_GRID_SIZES = [10, 20, 30, 40, 60] as const;
export type SnapGridSize = typeof SNAP_GRID_SIZES[number];

export const MACHINE_COLORS = [
  { stripe: '#4f46e5' }, // indigo
  { stripe: '#0d9488' }, // teal
  { stripe: '#ea580c' }, // orange
  { stripe: '#e11d48' }, // rose
  { stripe: '#059669' }, // emerald
  { stripe: '#7c3aed' }, // violet
  { stripe: '#0891b2' }, // cyan
  { stripe: '#d97706' }, // amber
] as const;

interface DesignState {
  /** Controls progressive disclosure in the Workflow Designer only. This is a presentation
   * preference, never a permission or part of the persisted Workflow Definition. */
  designerMode: DesignerMode;
  setDesignerMode: (mode: DesignerMode) => void;
  /** Visual language of the designer (Atelier redesign vs. classic look). */
  designerTheme: DesignerTheme;
  setDesignerTheme: (theme: DesignerTheme) => void;
  toggleDesignerTheme: () => void;
  /** Editor-header toolbar arrangement (compact grouped toolbar vs. classic inline-button row). */
  toolbarLayout: ToolbarLayout;
  setToolbarLayout: (layout: ToolbarLayout) => void;
  nodeStyle: NodeStyle;
  nodeIconStyle: NodeIconStyle;
  nodeScaleIndex: number;
  labelFontOffsetIndex: number;
  edgesAnimated: boolean;
  edgeWidthIndex: number;
  edgeRouting: EdgeRouting;
  flexiblePortsEnabled: boolean;
  /** When true, node ports stay hidden until the cursor nears the node (or it's selected / a
   *  connection is being dragged toward it) — keeps the canvas clean. false = ports always shown. */
  autoHidePorts: boolean;
  snapToGrid: boolean;
  snapGridSize: SnapGridSize;
  toggleNodeStyle: () => void;
  setNodeStyle: (style: NodeStyle) => void;
  /** Toggle setter used by the classic inline toolbar; `setNodeIconStyle` is the concrete-value
   *  setter used by the compact CanvasSettingsPanel segmented control. */
  toggleNodeIconStyle: () => void;
  setNodeIconStyle: (style: NodeIconStyle) => void;
  zoomIn: () => void;
  zoomOut: () => void;
  labelFontInc: () => void;
  labelFontDec: () => void;
  toggleEdgesAnimated: () => void;
  edgeWidthInc: () => void;
  edgeWidthDec: () => void;
  setEdgeRouting: (mode: EdgeRouting) => void;
  toggleFlexiblePorts: () => void;
  toggleAutoHidePorts: () => void;
  setSnapToGrid: (v: boolean) => void;
  setSnapGridSize: (v: SnapGridSize) => void;
  layoutMode: LayoutMode;
  setLayoutMode: (m: LayoutMode) => void;
  machineColoringEnabled: boolean;
  toggleMachineColoring: () => void;
  failureHeatmapEnabled: boolean;
  toggleFailureHeatmap: () => void;
  /** When true, edges show which upstream variables actually flow downstream (intersection
   *  of "what the source produces" and "what the target/transitive successors reference"). */
  dataFlowOverlayEnabled: boolean;
  toggleDataFlowOverlay: () => void;
  /** When true, the canvas tints each activity node by how often it executed in the last
   *  N days. Backed by `/api/workflows/{id}/coverage`. Window is configurable. */
  coverageHeatmapEnabled: boolean;
  toggleCoverageHeatmap: () => void;
  coverageWindowDays: number;
  setCoverageWindowDays: (days: number) => void;
  /** When true, the canvas highlights the critical path through the workflow DAG using
   *  p95 duration from step-stats. Nodes on the critical path get an orange glow;
   *  non-critical nodes show their slack time. */
  criticalPathEnabled: boolean;
  toggleCriticalPath: () => void;
  /** When true, the designer canvas uses the premium visual skin: dark depth shadows,
   *  glass gradient sheen, dual-level crosshatch grid, colored edge arrows + glow. */
  premiumCanvas: boolean;
  togglePremiumCanvas: () => void;
}

export const useDesignStore = create<DesignState>()(
  persist(
    (set) => ({
      designerMode: 'standard' as DesignerMode,
      setDesignerMode: (designerMode: DesignerMode) => set({ designerMode }),
      designerTheme: 'atelier' as DesignerTheme,
      setDesignerTheme: (designerTheme: DesignerTheme) => set({ designerTheme }),
      toggleDesignerTheme: () =>
        set((s) => ({ designerTheme: s.designerTheme === 'atelier' ? 'classic' : 'atelier' })),
      toolbarLayout: 'compact' as ToolbarLayout,
      setToolbarLayout: (toolbarLayout: ToolbarLayout) => set({ toolbarLayout }),
      nodeStyle: 'classic',
      nodeIconStyle: 'shape',
      nodeScaleIndex: 1, // sm default — xs rendered 33-node graphs as an unreadable dot cloud
      labelFontOffsetIndex: 2, // 0-offset — use base labelFont from NODE_SCALES
      edgesAnimated: true,
      edgeWidthIndex: 2, // 2px default — matches previous hardcoded width
      edgeRouting: 'smart' as EdgeRouting,
      flexiblePortsEnabled: false,
      autoHidePorts: true,
      snapToGrid: false,
      snapGridSize: 20 as SnapGridSize,
      setEdgeRouting: (mode: EdgeRouting) => set({ edgeRouting: mode }),
      toggleFlexiblePorts: () => set((s) => ({ flexiblePortsEnabled: !s.flexiblePortsEnabled })),
      toggleAutoHidePorts: () => set((s) => ({ autoHidePorts: !s.autoHidePorts })),
      setSnapToGrid: (v: boolean) => set({ snapToGrid: v }),
      setSnapGridSize: (v: SnapGridSize) => set({ snapGridSize: v }),
      layoutMode: 'LR' as LayoutMode,
      setLayoutMode: (m: LayoutMode) => set({ layoutMode: m }),
      machineColoringEnabled: false,
      toggleMachineColoring: () => set((s) => ({ machineColoringEnabled: !s.machineColoringEnabled })),
      failureHeatmapEnabled: false,
      toggleFailureHeatmap: () => set((s) => ({ failureHeatmapEnabled: !s.failureHeatmapEnabled })),
      dataFlowOverlayEnabled: false,
      toggleDataFlowOverlay: () => set((s) => ({ dataFlowOverlayEnabled: !s.dataFlowOverlayEnabled })),
      coverageHeatmapEnabled: false,
      toggleCoverageHeatmap: () => set((s) => ({ coverageHeatmapEnabled: !s.coverageHeatmapEnabled })),
      coverageWindowDays: 30,
      setCoverageWindowDays: (days: number) => set({ coverageWindowDays: Math.max(1, Math.min(365, days)) }),
      criticalPathEnabled: false,
      toggleCriticalPath: () => set((s) => ({ criticalPathEnabled: !s.criticalPathEnabled })),
      premiumCanvas: true,
      togglePremiumCanvas: () => set((s) => ({ premiumCanvas: !s.premiumCanvas })),
      toggleNodeStyle: () =>
        set((s) => ({ nodeStyle: s.nodeStyle === 'classic' ? 'card' : 'classic' })),
      setNodeStyle: (nodeStyle: NodeStyle) => set({ nodeStyle }),
      toggleNodeIconStyle: () =>
        set((s) => ({ nodeIconStyle: s.nodeIconStyle === 'shape' ? 'glyph' : 'shape' })),
      setNodeIconStyle: (nodeIconStyle: NodeIconStyle) => set({ nodeIconStyle }),
      zoomIn: () =>
        set((s) => ({ nodeScaleIndex: Math.min(s.nodeScaleIndex + 1, NODE_SCALES.length - 1) })),
      zoomOut: () =>
        set((s) => ({ nodeScaleIndex: Math.max(s.nodeScaleIndex - 1, 0) })),
      labelFontInc: () =>
        set((s) => ({ labelFontOffsetIndex: Math.min(s.labelFontOffsetIndex + 1, LABEL_FONT_OFFSETS.length - 1) })),
      labelFontDec: () =>
        set((s) => ({ labelFontOffsetIndex: Math.max(s.labelFontOffsetIndex - 1, 0) })),
      toggleEdgesAnimated: () =>
        set((s) => ({ edgesAnimated: !s.edgesAnimated })),
      edgeWidthInc: () =>
        set((s) => ({ edgeWidthIndex: Math.min(s.edgeWidthIndex + 1, EDGE_WIDTHS.length - 1) })),
      edgeWidthDec: () =>
        set((s) => ({ edgeWidthIndex: Math.max(s.edgeWidthIndex - 1, 0) })),
    }),
    {
      name: 'nodepilot-design',
      version: 1,
      // Profiles that already used the full designer retain the previous surface. Fresh
      // profiles use the standard-mode default above.
      migrate: (persisted, version) => {
        const state = persisted as Partial<DesignState>;
        if (version < 1 && !state.designerMode) return { ...state, designerMode: 'expert' };
        return state;
      },
    },
  ),
);
