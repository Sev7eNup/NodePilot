import { Concept, Fire, FlowModeler, Roadmap, Screen, type CarbonIconType } from '@carbon/icons-react';
import { useDesignStore } from '../../../stores/designStore';

type DesignState = ReturnType<typeof useDesignStore.getState>;

/**
 * The five canvas inspection overlays, defined once so both presentations stay in sync:
 * the compact {@link OverlaysMenu} (switch rows) and the classic inline toggle buttons
 * ({@link ClassicOverlayToggles}). Sharing the list keeps the `data-testid`s, i18n keys
 * and store wiring identical across both toolbar layouts. Label + title keys live in the
 * `editor` namespace.
 */
export interface OverlayDef {
  key: string;
  testId: string;
  icon: CarbonIconType;
  labelKey: string;
  onTitleKey: string;
  offTitleKey: string;
  selectEnabled: (s: DesignState) => boolean;
  selectToggle: (s: DesignState) => () => void;
}

export const OVERLAY_DEFS: readonly OverlayDef[] = [
  {
    // Screen is the app's machine icon (sidebar /machines). Network_3 already means network/LAN
    // here, and read as a near-twin of FlowModeler next to it in the toolbar.
    key: 'machineColoring', testId: 'toggle-machine-coloring', icon: Screen,
    labelKey: 'overlays.machineColoring', onTitleKey: 'machineColoringOn', offTitleKey: 'machineColoringOff',
    selectEnabled: (s) => s.machineColoringEnabled, selectToggle: (s) => s.toggleMachineColoring,
  },
  {
    key: 'failureHeatmap', testId: 'toggle-failure-heatmap', icon: Fire,
    labelKey: 'overlays.failureHeatmap', onTitleKey: 'failureHeatmapOn', offTitleKey: 'failureHeatmapOff',
    selectEnabled: (s) => s.failureHeatmapEnabled, selectToggle: (s) => s.toggleFailureHeatmap,
  },
  {
    key: 'dataFlow', testId: 'toggle-dataflow-overlay', icon: FlowModeler,
    labelKey: 'overlays.dataFlow', onTitleKey: 'dataFlowOverlayOn', offTitleKey: 'dataFlowOverlayOff',
    selectEnabled: (s) => s.dataFlowOverlayEnabled, selectToggle: (s) => s.toggleDataFlowOverlay,
  },
  {
    key: 'coverage', testId: 'toggle-coverage-heatmap', icon: Concept,
    labelKey: 'overlays.coverage', onTitleKey: 'coverageOn', offTitleKey: 'coverageOff',
    selectEnabled: (s) => s.coverageHeatmapEnabled, selectToggle: (s) => s.toggleCoverageHeatmap,
  },
  {
    key: 'criticalPath', testId: 'toggle-critical-path', icon: Roadmap,
    labelKey: 'overlays.criticalPath', onTitleKey: 'criticalPathOn', offTitleKey: 'criticalPathOff',
    selectEnabled: (s) => s.criticalPathEnabled, selectToggle: (s) => s.toggleCriticalPath,
  },
];
