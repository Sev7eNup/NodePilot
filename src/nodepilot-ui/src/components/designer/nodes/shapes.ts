import { TRIGGER_ACTIVITY_TYPES } from '../../../lib/activityCatalog.generated';
import type { EdgePortSide } from '../../../lib/edgePorts';

/**
 * Shape system for ActivityNode — visual categorization by outline shape, independent of colour.
 *
 * Trigger      → left-pointing pentagon (`pennant`); returnData → right-pointing pentagon
 *               (`flag`): together they form a bookend pair ◁ … ▷ (slate grey, NOT part of the
 *               control-flow group).
 * Control-flow → each gets its own shape (decision=`diamond`, junction=`hexLong`, forEach=`reel`,
 *               startWorkflow=`tagLeft`, see `CONTROL_SHAPE`) PLUS a shared indigo double-outline
 *               frame (ActivityNode `isControl` + `--np-controlflow-accent`) that visually sets the
 *               "control-flow" group apart from normal activities.
 * Every single **action** activity (+ `log`/`delay`) gets its OWN shape (see `ACTION_SHAPE`), so
 *               the node type is recognizable from its silhouette alone. The activity icon sits
 *               centered as a separate, NOT clipped layer on top → the icon itself stays fully
 *               readable, and the shape is an additional recognition cue.
 * `square`    → fallback for unknown/future types (no clip-path, normal render path).
 *
 * All special shapes are clip-path polygons. **Constraint:** every shape must touch the box's
 * edge midpoints on the left (0,50%) and right (100,50%), because that's where ReactFlow docks
 * ports/edges (`portHandleStyle` / `getPortPoint`). Where a shape's vertex is NOT at the edge
 * midpoint (e.g. a triangle's top point), `handleInset` pulls that side's handle inward onto
 * the silhouette.
 *
 * Selection/live-pulse rings can't use CSS `ring-*` (it doesn't follow clip-path); instead we
 * use a layering trick: an extra div with the same clip-path and a negative inset.
 *
 * The 21 action polygons were generated and visually validated via `scratchpad/gen-shapes.mjs`
 * (Bézier/vertex lists + a Playwright preview) — make geometry changes there.
 */

export const NODE_SHAPES = [
  // bookend / fallback
  'square', 'pennant', 'flag',
  // per-activity control-flow shapes (rendered with the shared indigo frame)
  'diamond', 'hexLong', 'reel', 'tagLeft',
  // per-activity action shapes
  'hexPointy', 'hexFlat', 'octagon', 'chamferedSquare', 'cross', 'starburst',
  'house', 'shield', 'blockArrow', 'chevronLeft', 'cylinder', 'pillH',
  'banner', 'plaque', 'kite', 'gem', 'pentagonUp', 'pentagonDown',
  'trapezoidUp', 'trapezoidDown', 'circle', 'speechBubble',
] as const;
export type NodeShape = typeof NODE_SHAPES[number];

/** CSS-Position-Properties für einen Badge-Slot. */
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
  /** clip-path polygon; `undefined` for `square` → normal (non-clipped) render path. */
  clip?: string;
  /** Multiplier on `scale.iconBox` (NODE_SCALES). */
  size: number;
  /** Multiplier on `scale.iconFont` in the special-shape render path. */
  iconScale: number;
  badges: BadgeProfile;
  handleInset?: HandleInset;
  /** Vertical offset of the activity icon as a fraction of the icon box (negative = upward).
   *  Defaults to 0 (bbox center). For shapes whose visual center isn't at the bbox center
   *  (e.g. speechBubble: body fills the upper 75%, tail points down → icon needs to shift up). */
  iconOffsetY?: number;
  /** Horizontal offset of the activity icon as a fraction of the icon box (negative = left).
   *  Defaults to 0. For the left/right-pointing bookend shapes (pennant/flag), whose visual
   *  center of mass sits off to one side of the bbox → shift the icon toward the body's center
   *  so it looks centered. */
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
  // `square` is the optical anchor: size 1.0, iconScale 1.0 → its inside-icon (iconFont) is the
  // equal-size target every other shape is tuned toward.
  square: { clip: undefined, size: 1.0, iconScale: 1.0, badges: SQUARE_BADGES },
  // Bookend pair (trigger + returnData). `size` area-compensated (pennant/flag fill ~62% of their
  // bbox → bumped to 1.25 so the silhouettes read equal to square); iconScale 1.0 → inside-icon
  // matches square. iconOffsetX shifts the icon toward the body's center of mass (pennant body
  // spans 25–100%, point at 0% → center ~56%; flag is the mirror, ~44%), mirrored like the shape.
  pennant: { clip: 'polygon(100% 0%, 25% 0%, 0% 50%, 25% 100%, 100% 100%)', size: 1.25, iconScale: 1.0, badges: PENNANT_BADGES, iconOffsetX: 0.06 },
  flag: { clip: 'polygon(0% 0%, 75% 0%, 100% 50%, 75% 100%, 0% 100%)', size: 1.25, iconScale: 1.0, badges: FLAG_BADGES, iconOffsetX: -0.06 },

  // Control-flow shapes (each rendered with the shared indigo frame). `size` area-compensated:
  // diamond fills 50% of its bbox → capped at 1.25; hexLong/reel/tagLeft tuned by visible area
  // (elongated hexLong keeps its width as a recognition cue, sized by height). iconScale 1.0.
  diamond: { clip: 'polygon(50% 0%, 100% 50%, 50% 100%, 0% 50%)', size: 1.25, iconScale: 1.0, badges: DIAMOND_BADGES, handleInset: { right: 0.02 } },
  hexLong: control('polygon(15% 0%, 85% 0%, 100% 50%, 85% 100%, 15% 100%, 0% 50%)', 1.10),          // junction (merge bar)
  reel: control('polygon(0% 0%, 42% 0%, 50% 13%, 58% 0%, 100% 0%, 100% 100%, 58% 100%, 50% 87%, 42% 100%, 0% 100%)', 1.20, 1.0, { top: 0.13, bottom: 0.13 }), // forEach (loop/reel)
  tagLeft: control('polygon(15% 0%, 100% 0%, 100% 100%, 15% 100%, 0% 50%)', 1.24),                  // startWorkflow (launch tag)

  // 21 per-activity action shapes (polygons generated via scratchpad/gen-shapes.mjs). `size` is
  // area-compensated (1/sqrt(visible-fill), capped at 1.25) so every silhouette reads equal;
  // `iconScale` 1.0 → inside-icon matches square. The ONLY two exceptions are `cross` and
  // `starburst`: their central inscribed region is too small to hold a full-size icon at a calm
  // footprint, so they keep a reduced iconScale (the documented "Sinthaftigkeit" trade-off —
  // equalizing them fully would balloon the node +30–55%).
  hexPointy: blob('polygon(50.0% 0.0%, 100.0% 25.0%, 100.0% 75.0%, 50.0% 100.0%, 0.0% 75.0%, 0.0% 25.0%)', 1.10),
  hexFlat: blob('polygon(25.0% 0.0%, 75.0% 0.0%, 100.0% 50.0%, 75.0% 100.0%, 25.0% 100.0%, 0.0% 50.0%)', 1.10),
  octagon: blob('polygon(30.0% 0.0%, 70.0% 0.0%, 100.0% 30.0%, 100.0% 70.0%, 70.0% 100.0%, 30.0% 100.0%, 0.0% 70.0%, 0.0% 30.0%)', 1.10),
  chamferedSquare: blob('polygon(16.0% 0.0%, 84.0% 0.0%, 100.0% 16.0%, 100.0% 84.0%, 84.0% 100.0%, 16.0% 100.0%, 0.0% 84.0%, 0.0% 16.0%)', 1.10),
  cross: blob('polygon(34.0% 0.0%, 66.0% 0.0%, 66.0% 34.0%, 100.0% 34.0%, 100.0% 66.0%, 66.0% 66.0%, 66.0% 100.0%, 34.0% 100.0%, 34.0% 66.0%, 0.0% 66.0%, 0.0% 34.0%, 34.0% 34.0%)', 1.25, 0.80),
  starburst: blob('polygon(50.0% 0.0%, 62.0% 38.0%, 100.0% 50.0%, 62.0% 62.0%, 50.0% 100.0%, 38.0% 62.0%, 0.0% 50.0%, 38.0% 38.0%)', 1.25, 0.88),
  house: blob('polygon(50.0% 0.0%, 100.0% 34.0%, 100.0% 100.0%, 0.0% 100.0%, 0.0% 34.0%)', 1.12, 1.0, { top: 0.34 }),
  // iconOffsetY: the shield tapers to a point at the bottom (50% 100%) → its visual center is
  // at ~40% y, so an icon placed at the bbox center (50%) sits too low; shift it up ~10% so it
  // reads as centered inside the shield.
  shield: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 55.0%, 50.0% 100.0%, 0.0% 55.0%)', 1.20, 1.0, { bottom: 0 }, -0.10),
  blockArrow: blob('polygon(0.0% 26.0%, 55.0% 26.0%, 55.0% 6.0%, 100.0% 50.0%, 55.0% 94.0%, 55.0% 74.0%, 0.0% 74.0%)', 1.25, 1.0, { top: 0.26, bottom: 0.26 }),
  chevronLeft: blob('polygon(100.0% 26.0%, 45.0% 26.0%, 45.0% 6.0%, 0.0% 50.0%, 45.0% 94.0%, 45.0% 74.0%, 100.0% 74.0%)', 1.25, 1.0, { top: 0.26, bottom: 0.26 }),
  cylinder: blob('polygon(0.0% 14.0%, 1.7% 10.9%, 6.7% 8.0%, 14.6% 5.5%, 25.0% 3.6%, 37.1% 2.4%, 50.0% 2.0%, 62.9% 2.4%, 75.0% 3.6%, 85.4% 5.5%, 93.3% 8.0%, 98.3% 10.9%, 100.0% 14.0%, 100.0% 14.0%, 100.0% 86.0%, 100.0% 86.0%, 98.3% 89.1%, 93.3% 92.0%, 85.4% 94.5%, 75.0% 96.4%, 62.9% 97.6%, 50.0% 98.0%, 37.1% 97.6%, 25.0% 96.4%, 14.6% 94.5%, 6.7% 92.0%, 1.7% 89.1%, 0.0% 86.0%, 0.0% 86.0%, 0.0% 14.0%)', 1.08, 1.0, { top: 0.14, bottom: 0.14 }),
  pillH: blob('polygon(12.0% 12.0%, 88.0% 12.0%, 88.0% 12.0%, 91.1% 13.3%, 94.0% 17.1%, 96.5% 23.1%, 98.4% 31.0%, 99.6% 40.2%, 100.0% 50.0%, 99.6% 59.8%, 98.4% 69.0%, 96.5% 76.9%, 94.0% 82.9%, 91.1% 86.7%, 88.0% 88.0%, 88.0% 88.0%, 12.0% 88.0%, 12.0% 88.0%, 8.9% 86.7%, 6.0% 82.9%, 3.5% 76.9%, 1.6% 69.0%, 0.4% 59.8%, 0.0% 50.0%, 0.4% 40.2%, 1.6% 31.0%, 3.5% 23.1%, 6.0% 17.1%, 8.9% 13.3%, 12.0% 12.0%)', 1.08, 1.0, { top: 0.12, bottom: 0.12 }),
  banner: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 100.0%, 50.0% 82.0%, 0.0% 100.0%)', 1.20, 1.0, { bottom: 0.18 }),
  plaque: blob('polygon(16.0% 0.0%, 100.0% 0.0%, 100.0% 84.0%, 84.0% 100.0%, 0.0% 100.0%, 0.0% 16.0%)', 1.04),
  kite: blob('polygon(50.0% 12.0%, 100.0% 50.0%, 50.0% 100.0%, 0.0% 50.0%)', 1.25, 1.0, { top: 0.12 }),
  gem: blob('polygon(28.0% 22.0%, 72.0% 22.0%, 100.0% 50.0%, 72.0% 78.0%, 28.0% 78.0%, 0.0% 50.0%)', 1.25, 1.0, { top: 0.22, bottom: 0.22 }),
  pentagonUp: blob('polygon(50.0% 0.0%, 100.0% 50.0%, 82.0% 100.0%, 18.0% 100.0%, 0.0% 50.0%)', 1.20, 1.0, { top: 0 }),
  // iconOffsetY: pentagonDown tapers to a point at the bottom (50% 100%) → its visual center is
  // at ~42% y, so an icon at the bbox center (50%) sits too low; shift it up ~8% (same idea as
  // shield/speechBubble).
  pentagonDown: blob('polygon(18.0% 0.0%, 82.0% 0.0%, 100.0% 50.0%, 50.0% 100.0%, 0.0% 50.0%)', 1.20, 1.0, { bottom: 0 }, -0.08),
  trapezoidUp: blob('polygon(28.0% 0.0%, 72.0% 0.0%, 100.0% 100.0%, 0.0% 100.0%)', 1.24, 1.0, { left: 0.13, right: 0.13, top: 0 }),
  trapezoidDown: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 72.0% 100.0%, 28.0% 100.0%)', 1.24, 1.0, { left: 0.13, right: 0.13, bottom: 0 }),
  circle: blob('polygon(50.0% 0.0%, 59.8% 1.0%, 69.1% 3.8%, 77.8% 8.4%, 85.4% 14.6%, 91.6% 22.2%, 96.2% 30.9%, 99.0% 40.2%, 100.0% 50.0%, 99.0% 59.8%, 96.2% 69.1%, 91.6% 77.8%, 85.4% 85.4%, 77.8% 91.6%, 69.1% 96.2%, 59.8% 99.0%, 50.0% 100.0%, 40.2% 99.0%, 30.9% 96.2%, 22.2% 91.6%, 14.6% 85.4%, 8.4% 77.8%, 3.8% 69.1%, 1.0% 59.8%, 0.0% 50.0%, 1.0% 40.2%, 3.8% 30.9%, 8.4% 22.2%, 14.6% 14.6%, 22.2% 8.4%, 30.9% 3.8%, 40.2% 1.0%)', 1.13),
  // llmQuery — chat/speech bubble with a tail (body fills the upper 75%; left+right edges reach the
  // vertical mid so ReactFlow ports dock cleanly). Distinct from the 8-point `starburst`.
  // iconOffsetY: Body center sits at 37.5% of the bbox (upper 75% body + lower-25% tail), but the
  // icon container centers at 50% → shift the icon up so it reads as centered in the bubble body.
  speechBubble: blob('polygon(0.0% 0.0%, 100.0% 0.0%, 100.0% 75.0%, 42.0% 75.0%, 26.0% 100.0%, 26.0% 75.0%, 0.0% 75.0%)', 1.15, 1.0, { bottom: 0.25 }, -0.12),
};

/** clip-path strings, derived from the registry (a single source of truth). */
export const SHAPE_CLIP_PATHS = Object.fromEntries(
  NODE_SHAPES.map((s) => [s, SHAPE_DEFS[s].clip]),
) as Record<NodeShape, string | undefined>;

// --- Mapping (checked at compile time) -----------------------------------
/** Exactly the activities that get their own shape (20 `action` types + `log` + `delay`). */
type ShapedActivityType =
  | 'runScript' | 'fileOperation' | 'folderOperation' | 'fileHash' | 'zipOperation'
  | 'serviceManagement' | 'scheduledTask' | 'registryOperation' | 'wmiQuery' | 'startProgram'
  | 'powerManagement' | 'waitForCondition' | 'restApi' | 'sql' | 'xmlQuery' | 'jsonQuery'
  | 'emailNotification' | 'textFileEdit' | 'generateText' | 'llmQuery' | 'log' | 'delay';

/** `satisfies` enforces that every ShapedActivityType is mapped and every value is a NodeShape. */
const ACTION_SHAPE = {
  runScript: 'hexPointy', fileOperation: 'plaque', folderOperation: 'trapezoidUp', fileHash: 'gem',
  zipOperation: 'chamferedSquare', serviceManagement: 'hexFlat', scheduledTask: 'pentagonUp',
  registryOperation: 'octagon', wmiQuery: 'pentagonDown', startProgram: 'blockArrow',
  powerManagement: 'starburst', waitForCondition: 'circle', restApi: 'chevronLeft', sql: 'cylinder',
  xmlQuery: 'kite', jsonQuery: 'trapezoidDown', emailNotification: 'banner', textFileEdit: 'house',
  generateText: 'pillH', llmQuery: 'speechBubble', log: 'shield', delay: 'cross',
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
