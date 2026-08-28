/**
 * Font size in flow pixels for a GroupNode title so it stays readable at any zoom.
 *
 * React Flow scales the whole canvas, so a fixed font shrinks to nothing when the user zooms out.
 * This puts a floor on the on-screen size instead: `fontSizeFlow * zoom` stays at or above
 * `minScreenPx`, and never drops below `baseFlowPx` at zoom 1.
 */
export function groupLabelFontSize(zoom: number, baseFlowPx = 16, minScreenPx = 13): number {
  const z = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  return Math.max(baseFlowPx, minScreenPx / z);
}
