/**
 * Each toolbar cluster glows in its own hue. Colors reference EXISTING theme tokens from
 * index.css (every one is declared for both light AND dark mode), so the proximity bloom
 * stays theme-correct in dark mode for free — no hard-coded hexes. The sectionColors
 * drift-test asserts each referenced var is declared in both a light and a dark scope.
 *
 * The compact toolbar uses `history | inspect | run | lifecycle`; the classic inline toolbar
 * additionally uses `layout | view | export` (its own separate clusters). Both share this map.
 */
export type SectionId =
  | 'history'
  | 'layout'
  | 'inspect'
  | 'view'
  | 'run'
  | 'lifecycle'
  | 'export';

export const SECTION_GLOW_COLOR: Record<SectionId, string> = {
  history: 'var(--color-secondary)', // undo/redo — muted blue-grey
  layout: 'var(--act-databaseTrigger-color)', // tidy/restore — violet
  inspect: 'var(--act-junction-color)', // search + View/Tools menus — indigo
  view: 'var(--act-wmiQuery-color)', // design-toggle/filter — cyan
  run: 'var(--act-manualTrigger-color)', // run/debug/lint/overlays — red
  lifecycle: 'var(--np-toolbar-accent)', // lock/save/publish/disable — skin-adaptive accent (distinct from run's red)
  export: 'var(--act-serviceManagement-color)', // json/png export — green
};
