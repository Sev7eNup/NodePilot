/**
 * The literal "Always" that older workflows carry in `edge.data.label`.
 *
 * It is no longer written and no longer drawn on the canvas, but two places still have to
 * recognise it: LabeledEdge skips it, and EdgePropertiesPanel treats it as auto-generated so a
 * later condition change replaces it.
 */
export const LEGACY_ALWAYS_LABEL = 'Always';

/**
 * Labels the properties panel is allowed to overwrite when the condition changes.
 * Any other label was typed by a user and is kept.
 */
export const CANONICAL_EDGE_LABELS: ReadonlySet<string> =
  new Set(['', LEGACY_ALWAYS_LABEL, 'On Success', 'On Failure']);
