/**
 * Font size (in flow px) for a GroupNode's title so it reads well at any zoom.
 *
 * React Flow scales the whole canvas, so a fixed font shrinks to nothing when the user zooms out.
 * We floor the ON-SCREEN size instead: `fontSizeFlow * zoom` is kept >= `minScreenPx`, while never
 * dropping below a comfortable `baseFlowPx` at zoom 1. Net effect — the label grows naturally when
 * zooming in and stays readable when zooming out.
 *
 * Examples (base 16, floor 13): zoom 1 → 16 (16px on screen); zoom 2 → 16 (32px on screen);
 * zoom 0.5 → 26 (13px on screen); zoom 0.25 → 52 (13px on screen).
 */
export function groupLabelFontSize(zoom: number, baseFlowPx = 16, minScreenPx = 13): number {
  const z = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  return Math.max(baseFlowPx, minScreenPx / z);
}
