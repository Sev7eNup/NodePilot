import { TRIGGER_ACTIVITY_TYPES } from '../../../lib/activityCatalog.generated';
import type { EdgePortSide } from '../../../lib/edgePorts';

/**
 * Shape system for ActivityNode — visual categorization by outline shape, independent of color.
 *
 * Trigger nodes use the left-pointing pentagon (`pennant`); returnData uses the right-pointing
 * pentagon (`flag`) — together a bookend pair, slate grey, not part of the control-flow group.
 * Control-flow activities each get their own shape (see `CONTROL_SHAPE`) plus a shared indigo
 * double-outline frame (ActivityNode `isControl` + `--np-controlflow-accent`). Every action
 * activity (plus `log`/`delay`) gets its own shape (see `ACTION_SHAPE`) so the node type is
 * recognizable from its silhouette alone; the icon renders as a separate, unclipped layer on top
 * so it stays fully readable while the shape adds a second recognition cue.
 * `square` is the fallback for unknown/future types (no clip-path, normal render path).
 *
 * All special shapes are clip-path polygons. Every shape must touch the box's edge midpoints on
 * the left (0,50%) and right (100,50%), because that's where ReactFlow docks ports/edges
 * (`portHandleStyle` / `getPortPoint`). Where a shape's vertex isn't at the edge midpoint (e.g. a
 * triangle's top point), `handleInset` pulls that side's handle inward onto the silhouette.
 *
 * Selection/live-pulse rings can't use CSS `ring-*` (it doesn't follow clip-path); instead they
 * use a layering trick: an extra div with the same clip-path and a negative inset.
 *
 * The 22 action polygons were generated and visually validated via `scratchpad/gen-shapes.mjs`
 * (Bézier/vertex lists + a Playwright preview) — make geometry changes there.
 */

export const NODE_SHAPES = [
  // bookend / fallback
  'square', 'pennant', 'flag',
  // per-activity control-flow shapes (rendered with the shared indigo frame)
  'diamond', 'hexLong', 'reel', 'tagLeft',
  // per-activity action shapes
  'hexPointy', 'hexFlat', 'octagon', 'chamferedSquare', 'stopwatch', 'power',
  'house', 'shield', 'launchSlant', 'browser', 'cylinder', 'pillH',
  'banner', 'documentFold', 'kite', 'gem', 'pentagonUp', 'pentagonDown',
  'folder', 'braces', 'circle', 'speechBubble',
] as const;
export type NodeShape = typeof NODE_SHAPES[number];

/** CSS position properties for a badge slot. */
export interface BadgePosition {
  top?: string;
  right?: string;
  left?: string;
  bottom?: string;
  transform?: string;
}

export interface BadgeProfile {
  topRight: BadgePosition;
  topLeft: BadgePosition;
  topMiddle: BadgePosition;
  bottomRight: BadgePosition;
}

/** Fraction (0..1) of the bounding box that a port handle is pulled inward, per side, onto the
 *  silhouette. A missing side defaults to 0 (handle sits at the bbox edge midpoint). */
export type HandleInset = Partial<Record<EdgePortSide, number>>;

export interface ShapeDef {
  /** clip-path polygon; `undefined` for `square` means the normal, non-clipped render path. */
  clip?: string;
  /** Optional solid body silhouette rendered behind `clip`. When set, the border/fill/halo layers
   *  clip to this shape (an opaque body), and `clip` is drawn on top as a colored accent outline.
   *  Used by `power`, whose `clip` is a hollow ring: the backing disc makes the node opaque while
   *  the ring stays the visible silhouette. `undefined` for every normal (single-layer) shape. */
  backingClip?: string;
  /** Multiplier on `scale.iconBox` (NODE_SCALES). */
  size: number;
  /** Multiplier on `scale.iconFont` in the special-shape render path. */
  iconScale: number;
  badges: BadgeProfile;
  handleInset?: HandleInset;
  /** Vertical offset of the activity icon as a fraction of the icon box (negative = upward).
   *  Defaults to 0 (bbox center). For shapes whose visual center isn't at the bbox center
   *  (e.g. speechBubble: body fills the upper 75%, tail points down, so the icon shifts up). */
  iconOffsetY?: number;
  /** Horizontal offset of the activity icon as a fraction of the icon box (negative = left).
   *  Defaults to 0. For the left/right-pointing bookend shapes (pennant/flag), whose visual
   *  center of mass sits off to one side of the bbox, so the icon shifts toward the body's
   *  center to look centered. */
  iconOffsetX?: number;
}

// --- Badge-Profile --------------------------------------------------------
const SQUARE_BADGES: BadgeProfile = {
  topRight: { top: '-4px', right: '-4px' },
  topLeft: { top: '-4px', left: '-4px' },
  topMiddle: { top: '-8px', left: '50%', transform: 'translateX(-50%)' },
  bottomRight: { bottom: '4px', right: '4px' },
};
// Generic profile for the action-blob shapes: pull badges inward, because the bbox corners are
// cut off on almost all these silhouettes (otherwise the badges would float freely beside them).
const BLOB_BADGES: BadgeProfile = {
  topRight: { top: '12%', right: '12%' },
  topLeft: { top: '12%', left: '12%' },
  topMiddle: { top: '-6px', left: '50%', transform: 'translateX(-50%)' },
  bottomRight: { bottom: '12%', right: '12%' },
};
const PENNANT_BADGES: BadgeProfile = {
  topRight: { top: '-4px', right: '-4px' },
  topLeft: { top: '-4px', left: '15%' },
  topMiddle: { top: '-8px', left: '60%', transform: 'translateX(-50%)' },
  bottomRight: { bottom: '-4px', right: '-4px' },
};
const DIAMOND_BADGES: BadgeProfile = {
  topRight: { top: '20%', right: '20%' },
  topLeft: { top: '20%', left: '20%' },
  topMiddle: { top: '-8px', left: '50%', transform: 'translateX(-50%)' },
  bottomRight: { bottom: '20%', right: '20%' },
};
const FLAG_BADGES: BadgeProfile = {
  topRight: { top: '-4px', right: '15%' },
  topLeft: { top: '-4px', left: '-4px' },
  topMiddle: { top: '-8px', left: '40%', transform: 'translateX(-50%)' },
  bottomRight: { bottom: '-4px', right: '15%' },
};

/** Shorthand for an action-blob shape. `size` is the per-shape bounding-box multiplier on
 *  `scale.iconBox`, area-compensated so every silhouette reads as optically equal in size
 *  (sparse shapes capped at 1.25 — see SHAPE_DEFS). `iconScale` is the DIRECT inside-icon size
 *  factor on `scale.iconFont` (1.0 = same px as a square node's icon; <1.0 only for the few
 *  silhouettes that can't hold a full-size icon at a calm footprint). */
const blob = (clip: string, size: number, iconScale = 1.0, handleInset?: HandleInset, iconOffsetY?: number): ShapeDef =>
  ({ clip, size, iconScale, badges: BLOB_BADGES, handleInset, iconOffsetY });

/** Shorthand for a control-flow shape: DIAMOND_BADGES (20% inset, since the corners are clipped);
 *  the shared control-flow frame is added in ActivityNode. `size`/`iconScale` as in `blob()`. */
const control = (clip: string, size: number, iconScale = 1.0, handleInset?: HandleInset): ShapeDef =>
  ({ clip, size, iconScale, badges: DIAMOND_BADGES, handleInset });

// --- Registry -------------------------------------------------------------
export const SHAPE_DEFS: Record<NodeShape, ShapeDef> = {
  // `square` is the optical anchor: size 1.0, iconScale 1.0 -> its inside-icon (iconFont) is the
  // equal-size target every other shape is tuned toward.
  square: { clip: undefined, size: 1.0, iconScale: 1.0, badges: SQUARE_BADGES },
  // Bookend pair (trigger + returnData). `size` area-compensated (pennant/flag fill ~62% of their
  // bbox -> bumped to 1.25 so the silhouettes read equal to square); iconScale 1.0 -> inside-icon
  // matches square. iconOffsetX shifts the icon toward the body's center of mass (pennant body
  // spans 25–100%, point at 0% -> center ~56%; flag is the mirror, ~44%), mirrored like the shape.
  pennant: { clip: 'polygon(100% 0%, 25% 0%, 0% 50%, 25% 100%, 100% 100%)', size: 1.25, iconScale: 1.0, badges: PENNANT_BADGES, iconOffsetX: 0.06 },
  flag: { clip: 'polygon(0% 0%, 75% 0%, 100% 50%, 75% 100%, 0% 100%)', size: 1.25, iconScale: 1.0, badges: FLAG_BADGES, iconOffsetX: -0.06 },

  // Control-flow shapes (each rendered with the shared indigo frame). `size` area-compensated:
  // diamond fills 50% of its bbox -> capped at 1.25; hexLong/reel/tagLeft tuned by visible area
  // (elongated hexLong keeps its width as a recognition cue, sized by height). iconScale 1.0.
  diamond: { clip: 'polygon(50% 0%, 100% 50%, 50% 100%, 0% 50%)', size: 1.25, iconScale: 1.0, badges: DIAMOND_BADGES, handleInset: { right: 0.02 } },
  hexLong: control('polygon(15% 0%, 85% 0%, 100% 50%, 85% 100%, 15% 100%, 0% 50%)', 1.10),          // junction (merge bar)
  reel: control('polygon(0% 0%, 42% 0%, 50% 13%, 58% 0%, 100% 0%, 100% 100%, 58% 100%, 50% 87%, 42% 100%, 0% 100%)', 1.20, 1.0, { top: 0.13, bottom: 0.13 }), // forEach (loop/reel)
  tagLeft: control('polygon(15% 0%, 100% 0%, 100% 100%, 15% 100%, 0% 50%)', 1.24),                  // startWorkflow (launch tag)

  // 22 per-activity action shapes (polygons generated via scratchpad/gen-shapes.mjs). `size` is
  // area-compensated (1/sqrt(visible-fill), capped at 1.25) so every silhouette reads equal;
  // `iconScale` 1.0 -> inside-icon matches square. One deliberate per-shape override: `stopwatch`
  // is ENLARGED so the clock icon fills the round watch face (a standard icon looks lost on a
  // dial).
  hexPointy: blob('polygon(50.0% 0.0%, 100.0% 25.0%, 100.0% 75.0%, 50.0% 100.0%, 0.0% 75.0%, 0.0% 25.0%)', 1.10),
  hexFlat: blob('polygon(25.0% 0.0%, 75.0% 0.0%, 100.0% 50.0%, 75.0% 100.0%, 25.0% 100.0%, 0.0% 50.0%)', 1.10),
  octagon: blob('polygon(30.0% 0.0%, 70.0% 0.0%, 100.0% 30.0%, 100.0% 70.0%, 70.0% 100.0%, 30.0% 100.0%, 0.0% 70.0%, 0.0% 30.0%)', 1.10),
  chamferedSquare: blob('polygon(16.0% 0.0%, 84.0% 0.0%, 100.0% 16.0%, 100.0% 84.0%, 84.0% 100.0%, 16.0% 100.0%, 0.0% 84.0%, 0.0% 16.0%)', 1.10),
  // delay — stopwatch: round body (circle, center y=56 %) with 3 knobs on top, thematically
  // fitting delay's `schedule` (clock) icon. Distinct from waitForCondition (plain circle +
  // hourglass icon) thanks to the knobs. The center crown sticks STRAIGHT UP (x 44–56, y 0–12);
  // the left/right buttons stick out DIAGONALLY from the case (angled outward like a real
  // stopwatch's side pushers — parallelograms leaning up-left / up-right, NOT vertical pins). The
  // round body makes a standard-size icon look lost on the watch face, so iconScale is ENLARGED
  // (1.25) to fill the dial (still within the inscribed square); iconOffsetY 0.06 seats the icon
  // at the circle's center. The circle's left/right edge sits at ~6 % inset -> handleInset pulls
  // the side ports onto the silhouette; top port lands on the center crown, bottom port on the
  // circle's tangent.
  stopwatch: blob('polygon(6% 56%, 6.4% 62.1%, 7.7% 68.1%, 9.8% 73.9%, 12.7% 79.3%, 16.3% 84.3%, 20.6% 88.7%, 25.4% 92.5%, 30.7% 95.5%, 36.4% 97.8%, 42.4% 99.3%, 48.5% 100%, 54.6% 99.8%, 60.6% 98.7%, 66.5% 96.8%, 72% 94.1%, 77.1% 90.7%, 81.7% 86.6%, 85.6% 81.9%, 88.8% 76.7%, 91.3% 71%, 93% 65.1%, 93.9% 59.1%, 94% 56%, 93.6% 49.9%, 92.3% 43.9%, 90.2% 38.1%, 87.3% 32.7%, 83.7% 27.7%, 79.4% 23.3%, 78.3% 22.3%, 87.3% 14.3%, 79.7% 9.2%, 70.7% 17.2%, 65% 14.7%, 59.1% 13%, 56.1% 12.4%, 56.1% 0%, 43.9% 0%, 43.9% 12.4%, 37.9% 13.7%, 32.1% 15.8%, 29.3% 17.2%, 20.3% 9.2%, 12.7% 14.3%, 21.7% 22.3%, 17.3% 26.6%, 13.5% 31.4%, 10.5% 36.7%, 8.2% 42.4%, 6.7% 48.4%, 6% 54.5%)', 1.23, 1.25, { left: 0.06, right: 0.06 }, 0.06),
  // powerManagement — "ring on a filled disc": the visible silhouette (`clip`) is the IEC power
  // glyph — a HOLLOW ring broken at the top with a bar in the gap — drawn as a coloured accent on
  // top of a solid backing disc (`backingClip`, concentric, same 0.44 radius) so the node reads as
  // an opaque round body AND as a power symbol. Both centre on y=56 %; the disc's widest span sits
  // below the y=50 % port line -> handleInset pulls the side ports (~6.4 %) onto the disc, the top
  // port onto the bar. iconScale 0.9 keeps the (kept) activity glyph large inside the ring's hole;
  // iconOffsetY +0.06 seats it on the disc's centre. The ring polygon: outer arc + inner arc walked
  // back (one closed C-contour), then the bar as a second loop via a zero-width bridge (evenodd).
  power: {
    clip: 'polygon(evenodd, 65.8% 14.9%, 70.6% 17.1%, 75.1% 19.9%, 79.3% 23.2%, 83.0% 26.9%, 86.3% 31.1%, 89.0% 35.7%, 91.2% 40.5%, 92.7% 45.6%, 93.7% 50.8%, 94.0% 56.1%, 93.7% 61.4%, 92.7% 66.6%, 91.1% 71.7%, 88.9% 76.5%, 86.2% 81.0%, 82.9% 85.2%, 79.1% 89.0%, 75.0% 92.2%, 70.4% 95.0%, 65.6% 97.1%, 60.5% 98.7%, 55.3% 99.7%, 50.0% 100.0%, 44.7% 99.7%, 39.5% 98.7%, 34.4% 97.1%, 29.6% 95.0%, 25.0% 92.2%, 20.9% 89.0%, 17.1% 85.2%, 13.8% 81.0%, 11.1% 76.5%, 8.9% 71.7%, 7.3% 66.6%, 6.3% 61.4%, 6.0% 56.1%, 6.3% 50.8%, 7.3% 45.6%, 8.8% 40.5%, 11.0% 35.7%, 13.7% 31.1%, 17.0% 26.9%, 20.7% 23.2%, 24.9% 19.9%, 29.4% 17.1%, 34.2% 14.9%, 39.2% 28.0%, 36.0% 29.5%, 32.9% 31.4%, 30.0% 33.6%, 27.5% 36.2%, 25.3% 39.0%, 23.4% 42.1%, 21.9% 45.4%, 20.9% 48.9%, 20.2% 52.5%, 20.0% 56.1%, 20.2% 59.7%, 20.9% 63.2%, 22.0% 66.7%, 23.5% 70.0%, 25.3% 73.1%, 27.6% 75.9%, 30.1% 78.5%, 33.0% 80.7%, 36.1% 82.6%, 39.4% 84.1%, 42.8% 85.1%, 46.4% 85.8%, 50.0% 86.0%, 53.6% 85.8%, 57.2% 85.1%, 60.6% 84.1%, 63.9% 82.6%, 67.0% 80.7%, 69.9% 78.5%, 72.4% 75.9%, 74.7% 73.1%, 76.5% 70.0%, 78.0% 66.7%, 79.1% 63.2%, 79.8% 59.7%, 80.0% 56.1%, 79.8% 52.5%, 79.1% 48.9%, 78.1% 45.4%, 76.6% 42.1%, 74.7% 39.0%, 72.5% 36.2%, 70.0% 33.6%, 67.1% 31.4%, 64.0% 29.5%, 60.8% 28.0%, 44.2% 8.0%, 47.2% 5.0%, 52.8% 5.0%, 55.8% 8.0%, 55.8% 55.5%, 52.8% 58.5%, 47.2% 58.5%, 44.2% 55.5%, 44.2% 8.0%, 60.8% 28.0%)',
    backingClip: 'polygon(50.0% 12.0%, 56.9% 12.5%, 63.6% 14.2%, 70.0% 16.8%, 75.9% 20.4%, 81.1% 24.9%, 85.6% 30.1%, 89.2% 36.0%, 91.8% 42.4%, 93.5% 49.1%, 94.0% 56.0%, 93.5% 62.9%, 91.8% 69.6%, 89.2% 76.0%, 85.6% 81.9%, 81.1% 87.1%, 75.9% 91.6%, 70.0% 95.2%, 63.6% 97.8%, 56.9% 99.5%, 50.0% 100.0%, 43.1% 99.5%, 36.4% 97.8%, 30.0% 95.2%, 24.1% 91.6%, 18.9% 87.1%, 14.4% 81.9%, 10.8% 76.0%, 8.2% 69.6%, 6.5% 62.9%, 6.0% 56.0%, 6.5% 49.1%, 8.2% 42.4%, 10.8% 36.0%, 14.4% 30.1%, 18.9% 24.9%, 24.1% 20.4%, 30.0% 16.8%, 36.4% 14.2%, 43.1% 12.5%)',
    size: 1.13, iconScale: 0.9, badges: BLOB_BADGES,
    handleInset: { left: 0.064, right: 0.064, top: 0.05 }, iconOffsetY: 0.06,
  },
  house: blob('polygon(50.0% 0.0%, 100.0% 34.0%, 100.0% 100.0%, 0.0% 100.0%, 0.0% 34.0%)', 1.12, 1.0, { top: 0.34 }),
  // iconOffsetY: the shield tapers to a point at the bottom (50% 100%) -> its visual center is
  // at ~40% y, so an icon placed at the bbox center (50%) sits too low; shift it up ~10% so it
  // reads as centered inside the shield.
  shield: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 55.0%, 50.0% 100.0%, 0.0% 55.0%)', 1.20, 1.0, { bottom: 0 }, -0.10),
  // startProgram — a clean north-east launch slant. It echoes the Carbon `Rocket` glyph's motion
  // without imitating a rocket or borrowing Start Workflow's play semantics/tag silhouette.
  launchSlant: {
    clip: 'polygon(25.0% 0.0%, 100.0% 0.0%, 75.0% 100.0%, 0.0% 100.0%)',
    size: 1.18,
    iconScale: 1.14,
    badges: BLOB_BADGES,
    handleInset: { left: 0.125, right: 0.125 },
  },
  // restApi — browser window with a raised tab/address-bar crown. This replaces the former
  // left-pointing chevron: an HTTP request is a web interaction, not backwards navigation.
  browser: blob('polygon(10.0% 12.0%, 28.0% 12.0%, 34.0% 0.0%, 70.0% 0.0%, 76.0% 12.0%, 90.0% 12.0%, 100.0% 22.0%, 100.0% 90.0%, 90.0% 100.0%, 10.0% 100.0%, 0.0% 90.0%, 0.0% 22.0%)', 1.14, 1.05),
  cylinder: blob('polygon(0.0% 14.0%, 1.7% 10.9%, 6.7% 8.0%, 14.6% 5.5%, 25.0% 3.6%, 37.1% 2.4%, 50.0% 2.0%, 62.9% 2.4%, 75.0% 3.6%, 85.4% 5.5%, 93.3% 8.0%, 98.3% 10.9%, 100.0% 14.0%, 100.0% 14.0%, 100.0% 86.0%, 100.0% 86.0%, 98.3% 89.1%, 93.3% 92.0%, 85.4% 94.5%, 75.0% 96.4%, 62.9% 97.6%, 50.0% 98.0%, 37.1% 97.6%, 25.0% 96.4%, 14.6% 94.5%, 6.7% 92.0%, 1.7% 89.1%, 0.0% 86.0%, 0.0% 86.0%, 0.0% 14.0%)', 1.08, 1.0, { top: 0.14, bottom: 0.14 }),
  pillH: blob('polygon(12.0% 4.0%, 88.0% 4.0%, 91.1% 5.6%, 94.0% 10.2%, 96.5% 17.4%, 98.4% 26.7%, 99.6% 37.6%, 100.0% 50.0%, 99.6% 62.4%, 98.4% 73.3%, 96.5% 82.6%, 94.0% 89.8%, 91.1% 94.4%, 88.0% 96.0%, 12.0% 96.0%, 8.9% 94.4%, 6.0% 89.8%, 3.5% 82.6%, 1.6% 73.3%, 0.4% 62.4%, 0.0% 50.0%, 0.4% 37.6%, 1.6% 26.7%, 3.5% 17.4%, 6.0% 10.2%, 8.9% 5.6%)', 1.16, 1.12, { top: 0.04, bottom: 0.04 }),
  banner: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 100.0%, 50.0% 82.0%, 0.0% 100.0%)', 1.20, 1.0, { bottom: 0.18 }),
  // fileOperation — document silhouette matching Carbon's `Document` glyph: a straight page
  // body with the characteristic folded corner at the upper right.
  documentFold: blob('polygon(0.0% 0.0%, 72.0% 0.0%, 100.0% 28.0%, 100.0% 100.0%, 0.0% 100.0%)', 1.08),
  kite: blob('polygon(50.0% 4.0%, 100.0% 50.0%, 50.0% 100.0%, 0.0% 50.0%)', 1.30, 1.08, { top: 0.04 }),
  gem: blob('polygon(28.0% 12.0%, 72.0% 12.0%, 100.0% 50.0%, 72.0% 88.0%, 28.0% 88.0%, 0.0% 50.0%)', 1.28, 1.12, { top: 0.12, bottom: 0.12 }),
  pentagonUp: blob('polygon(50.0% 0.0%, 100.0% 50.0%, 82.0% 100.0%, 18.0% 100.0%, 0.0% 50.0%)', 1.20, 1.0, { top: 0 }),
  // iconOffsetY: pentagonDown tapers to a point at the bottom (50% 100%) -> its visual center is
  // at ~42% y, so an icon at the bbox center (50%) sits too low; shift it up ~8% (same idea as
  // shield/speechBubble).
  pentagonDown: blob('polygon(18.0% 0.0%, 82.0% 0.0%, 100.0% 50.0%, 50.0% 100.0%, 0.0% 50.0%)', 1.20, 1.0, { bottom: 0 }, -0.08),
  // folderOperation — familiar Windows-style folder silhouette: a raised tab on the upper left,
  // followed by the angled shoulder into the full-width folder body. The icon is seated slightly
  // lower in the body, while the top handle follows the shoulder instead of floating above it.
  folder: blob(
    'polygon(0.0% 14.0%, 8.0% 14.0%, 8.0% 4.0%, 42.0% 4.0%, 54.0% 18.0%, 100.0% 18.0%, 100.0% 100.0%, 0.0% 100.0%)',
    1.10,
    1.0,
    { top: 0.14 },
    0.06,
  ),
  // jsonQuery — mirrored curly-brace silhouette: the stepped shoulders and centre tips reinforce
  // the `{ … }` glyph inside. The centre tips deliberately reach both horizontal edge midpoints
  // for clean ReactFlow ports.
  braces: blob('polygon(18.0% 0.0%, 82.0% 0.0%, 82.0% 14.0%, 94.0% 14.0%, 94.0% 38.0%, 100.0% 50.0%, 94.0% 62.0%, 94.0% 86.0%, 82.0% 86.0%, 82.0% 100.0%, 18.0% 100.0%, 18.0% 86.0%, 6.0% 86.0%, 6.0% 62.0%, 0.0% 50.0%, 6.0% 38.0%, 6.0% 14.0%, 18.0% 14.0%)', 1.18, 1.05),
  circle: blob('polygon(50.0% 0.0%, 59.8% 1.0%, 69.1% 3.8%, 77.8% 8.4%, 85.4% 14.6%, 91.6% 22.2%, 96.2% 30.9%, 99.0% 40.2%, 100.0% 50.0%, 99.0% 59.8%, 96.2% 69.1%, 91.6% 77.8%, 85.4% 85.4%, 77.8% 91.6%, 69.1% 96.2%, 59.8% 99.0%, 50.0% 100.0%, 40.2% 99.0%, 30.9% 96.2%, 22.2% 91.6%, 14.6% 85.4%, 8.4% 77.8%, 3.8% 69.1%, 1.0% 59.8%, 0.0% 50.0%, 1.0% 40.2%, 3.8% 30.9%, 8.4% 22.2%, 14.6% 14.6%, 22.2% 8.4%, 30.9% 3.8%, 40.2% 1.0%)', 1.13),
  // llmQuery — chat/speech bubble with a tail (body fills the upper 75%; left+right edges reach the
  // vertical mid so ReactFlow ports dock cleanly).
  // iconOffsetY: Body center sits at 37.5% of the bbox (upper 75% body + lower-25% tail), but the
  // icon container centers at 50% -> shift the icon up so it reads as centered in the bubble body.
  speechBubble: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 75.0%, 42.0% 75.0%, 26.0% 100.0%, 26.0% 75.0%, 0.0% 75.0%)', 1.15, 1.0, { bottom: 0.25 }, -0.12),
};

/** clip-path strings, derived from the registry (a single source of truth). */
export const SHAPE_CLIP_PATHS = Object.fromEntries(
  NODE_SHAPES.map((s) => [s, SHAPE_DEFS[s].clip]),
) as Record<NodeShape, string | undefined>;

/** Optional solid-body clip drawn BEHIND the silhouette (only `power` today). `undefined` -> the
 *  shape renders as a single layer clipped to `SHAPE_CLIP_PATHS[shape]`. */
export const getBackingClip = (shape: NodeShape): string | undefined => SHAPE_DEFS[shape].backingClip;

// --- Mapping (checked at compile time) -----------------------------------
/** Exactly the activities that get their own shape (20 `action` types + `log` + `delay`). */
type ShapedActivityType =
  | 'runScript' | 'fileOperation' | 'folderOperation' | 'fileHash' | 'zipOperation'
  | 'serviceManagement' | 'scheduledTask' | 'registryOperation' | 'wmiQuery' | 'startProgram'
  | 'powerManagement' | 'waitForCondition' | 'restApi' | 'sql' | 'xmlQuery' | 'jsonQuery'
  | 'emailNotification' | 'textFileEdit' | 'generateText' | 'llmQuery' | 'log' | 'delay';

/** `satisfies` enforces that every ShapedActivityType is mapped and every value is a NodeShape. */
const ACTION_SHAPE = {
  runScript: 'hexPointy', fileOperation: 'documentFold', folderOperation: 'folder', fileHash: 'gem',
  zipOperation: 'chamferedSquare', serviceManagement: 'hexFlat', scheduledTask: 'pentagonUp',
  registryOperation: 'octagon', wmiQuery: 'pentagonDown', startProgram: 'launchSlant',
  powerManagement: 'power', waitForCondition: 'circle', restApi: 'browser', sql: 'cylinder',
  xmlQuery: 'kite', jsonQuery: 'braces', emailNotification: 'banner', textFileEdit: 'house',
  generateText: 'pillH', llmQuery: 'speechBubble', log: 'shield', delay: 'stopwatch',
} as const satisfies Record<ShapedActivityType, NodeShape>;

/** Control-flow activities — each gets its own shape; all render with the shared indigo frame.
 *  `returnData` is intentionally NOT here (it stays the slate `flag` bookend). */
type ControlActivityType = 'decision' | 'junction' | 'forEach' | 'startWorkflow';
const CONTROL_SHAPE = {
  decision: 'diamond', junction: 'hexLong', forEach: 'reel', startWorkflow: 'tagLeft',
} as const satisfies Record<ControlActivityType, NodeShape>;

/** The shapes that belong to the control-flow group (drive the shared frame off the shape, so it
 *  structurally excludes the `flag`/`pennant` bookends). Mirror of `CONTROL_SHAPE`'s values. */
const CONTROL_SHAPE_SET = new Set<NodeShape>(Object.values(CONTROL_SHAPE));
export const isControlFlowShape = (shape: NodeShape): boolean => CONTROL_SHAPE_SET.has(shape);

export function getNodeShape(activityType: string): NodeShape {
  if (TRIGGER_ACTIVITY_TYPES.has(activityType)) return 'pennant';
  const control = (CONTROL_SHAPE as Record<string, NodeShape>)[activityType];
  if (control) return control;
  if (activityType === 'returnData') return 'flag';
  return (ACTION_SHAPE as Record<string, NodeShape>)[activityType] ?? 'square';
}

// --- thin readers over the registry --------------------------------------
export const getNodeSizeMultiplier = (shape: NodeShape): number => SHAPE_DEFS[shape].size;
export const getIconScaleMultiplier = (shape: NodeShape): number => SHAPE_DEFS[shape].iconScale;
export const getBadgePositions = (shape: NodeShape): BadgeProfile => SHAPE_DEFS[shape].badges;
export const getHandleInset = (shape: NodeShape): HandleInset => SHAPE_DEFS[shape].handleInset ?? {};
/** Vertical icon offset as a fraction of the icon box (negative = upward). 0 for most shapes. */
export const getIconOffsetY = (shape: NodeShape): number => SHAPE_DEFS[shape].iconOffsetY ?? 0;
/** Horizontal icon offset as a fraction of the icon box (negative = left). 0 for most shapes. */
export const getIconOffsetX = (shape: NodeShape): number => SHAPE_DEFS[shape].iconOffsetX ?? 0;
