/* Geometry for the architecture diagram on the home page. Boxes are laid out per breakpoint and
   connected by orthogonal links; main.ts writes the coordinates into the markup. */

export const BOX_KEYS = ['client', 'triggers', 'service', 'database', 'targets'] as const
export type BoxKey = (typeof BOX_KEYS)[number]

export const LINK_KEYS = ['trigger', 'winrm', 'store', 'live'] as const
export type LinkKey = (typeof LINK_KEYS)[number]

/** The six trigger types, in the order the docs list them. */
export const TRIGGER_KEYS = ['manual', 'schedule', 'webhook', 'file', 'database', 'eventlog'] as const

/** The parts that share the one service process (ADR 0010). */
export const SERVICE_PARTS = ['api', 'engine', 'scheduler'] as const

/** Target machines drawn inside the targets box. */
export const MACHINE_COUNT = 3

export type Side = 'left' | 'right' | 'top' | 'bottom'

/** A type alias, not an interface: only an alias is assignable to the attribute maps in main.ts. */
export type Box = {
  x: number
  y: number
  width: number
  height: number
}

export interface Point {
  x: number
  y: number
}

export interface LinkSpec {
  from: BoxKey
  fromSide: Side
  /** Position on the source side, 0..1 from its start. Defaults to the middle. */
  fromOffset?: number
  to: BoxKey
  toSide: Side
  toOffset?: number
  /** Coordinate of the middle segment where both ends leave along the same axis. */
  bend?: number
}

export interface Metrics {
  /** Space above the first row inside a box with a title. */
  header: number
  /** Distance between two trigger rows. */
  triggerStep: number
  machineHeight: number
  machineGap: number
  chipHeight: number
  chipGap: number
  /** Padding on the left and right inside a box. */
  inset: number
}

export interface TopologyLayout {
  width: number
  height: number
  boxes: Record<BoxKey, Box>
  metrics: Metrics
}

const WIDE_METRICS: Metrics = {
  header: 34,
  triggerStep: 28,
  machineHeight: 38,
  machineGap: 9,
  chipHeight: 26,
  chipGap: 8,
  inset: 12,
}

const COMPACT_METRICS: Metrics = {
  header: 38,
  triggerStep: 26,
  machineHeight: 38,
  machineGap: 8,
  chipHeight: 28,
  chipGap: 8,
  inset: 12,
}

/**
 * Wide: triggers on the left, the one service in the middle over its database, targets on the
 * right, and the client above — so the SignalR link runs back over the top without crossing.
 * Compact: everything stacked, with the two links that cannot run straight down using the free
 * margins, SignalR up the left and WinRM down the right.
 */
export const LAYOUTS: { wide: TopologyLayout; compact: TopologyLayout } = {
  wide: {
    width: 960,
    height: 344,
    boxes: {
      client: { x: 16, y: 8, width: 210, height: 46 },
      triggers: { x: 16, y: 86, width: 186, height: 214 },
      service: { x: 344, y: 88, width: 272, height: 124 },
      database: { x: 376, y: 268, width: 208, height: 52 },
      targets: { x: 744, y: 86, width: 200, height: 214 },
    },
    metrics: WIDE_METRICS,
  },
  compact: {
    width: 400,
    height: 820,
    boxes: {
      client: { x: 24, y: 8, width: 352, height: 46 },
      triggers: { x: 80, y: 86, width: 240, height: 200 },
      service: { x: 44, y: 330, width: 312, height: 124 },
      database: { x: 56, y: 502, width: 288, height: 52 },
      targets: { x: 24, y: 602, width: 352, height: 200 },
    },
    metrics: COMPACT_METRICS,
  },
}

/**
 * Where each link starts and ends. In the compact layout the two side routes enter through a
 * port near the far corner, so their first segment leaves the service box rather than crossing it.
 */
export const LINKS: Record<LinkKey, Record<'wide' | 'compact', LinkSpec>> = {
  trigger: {
    wide: { from: 'triggers', fromSide: 'right', to: 'service', toSide: 'left' },
    compact: { from: 'triggers', fromSide: 'bottom', to: 'service', toSide: 'top' },
  },
  winrm: {
    wide: { from: 'service', fromSide: 'right', to: 'targets', toSide: 'left' },
    compact: { from: 'service', fromSide: 'right', to: 'targets', toSide: 'top', toOffset: 0.95 },
  },
  store: {
    wide: { from: 'service', fromSide: 'bottom', to: 'database', toSide: 'top' },
    compact: { from: 'service', fromSide: 'bottom', to: 'database', toSide: 'top' },
  },
  live: {
    wide: { from: 'service', fromSide: 'top', to: 'client', toSide: 'right' },
    compact: { from: 'service', fromSide: 'left', to: 'client', toSide: 'bottom', toOffset: 0.04 },
  },
}

/** Arrow length and half width in canvas units. */
const ARROW = { length: 9, halfWidth: 4.5 }

function round(value: number): number {
  return Math.round(value * 10) / 10
}

/** The point on a box side, and the outward direction there. */
export function port(box: Box, side: Side, offset = 0.5): Point {
  switch (side) {
    case 'left':
      return { x: box.x, y: box.y + box.height * offset }
    case 'right':
      return { x: box.x + box.width, y: box.y + box.height * offset }
    case 'top':
      return { x: box.x + box.width * offset, y: box.y }
    case 'bottom':
      return { x: box.x + box.width * offset, y: box.y + box.height }
  }
}

const NORMAL: Record<Side, Point> = {
  left: { x: -1, y: 0 },
  right: { x: 1, y: 0 },
  top: { x: 0, y: -1 },
  bottom: { x: 0, y: 1 },
}

export interface LinkGeometry {
  path: string
  arrow: string
  labelX: number
  labelY: number
}

function polyline(points: readonly Point[]): string {
  const [first, ...rest] = points
  return `M${round(first.x)} ${round(first.y)}` + rest.map((point) => `L${round(point.x)} ${round(point.y)}`).join('')
}

/** The point halfway along a polyline, measured by length. */
function midpoint(points: readonly Point[]): Point {
  const lengths = points.slice(1).map((point, i) => Math.hypot(point.x - points[i].x, point.y - points[i].y))
  let remaining = lengths.reduce((sum, length) => sum + length, 0) / 2
  for (const [i, length] of lengths.entries()) {
    if (remaining > length) {
      remaining -= length
      continue
    }
    const share = length === 0 ? 0 : remaining / length
    return {
      x: points[i].x + (points[i + 1].x - points[i].x) * share,
      y: points[i].y + (points[i + 1].y - points[i].y) * share,
    }
  }
  return points[points.length - 1]
}

/**
 * An orthogonal link between two boxes. Both ends leave along their side's normal; where those
 * normals are perpendicular the link bends once, where they share an axis it bends twice. The
 * line stops inside the arrowhead so its end does not show past the tip.
 */
export function linkGeometry(layout: TopologyLayout, spec: LinkSpec): LinkGeometry {
  const start = port(layout.boxes[spec.from], spec.fromSide, spec.fromOffset)
  const tip = port(layout.boxes[spec.to], spec.toSide, spec.toOffset)
  const into = NORMAL[spec.toSide]
  const end = { x: tip.x + into.x * ARROW.length, y: tip.y + into.y * ARROW.length }
  const out = NORMAL[spec.fromSide]

  const horizontalOut = out.y === 0
  const horizontalIn = into.y === 0
  let points: Point[]
  if (horizontalOut !== horizontalIn) {
    // Perpendicular: one corner, leaving along the source normal.
    points = [start, horizontalOut ? { x: end.x, y: start.y } : { x: start.x, y: end.y }, end]
  } else if (horizontalOut ? start.y === end.y : start.x === end.x) {
    points = [start, end]
  } else if (horizontalOut) {
    const bend = spec.bend ?? (start.x + end.x) / 2
    points = [start, { x: bend, y: start.y }, { x: bend, y: end.y }, end]
  } else {
    const bend = spec.bend ?? (start.y + end.y) / 2
    points = [start, { x: start.x, y: bend }, { x: end.x, y: bend }, end]
  }

  // The arrowhead sits on the target port, pointing the way the link enters the box.
  const baseX = tip.x + into.x * ARROW.length
  const baseY = tip.y + into.y * ARROW.length
  const arrow = [
    `${round(tip.x)},${round(tip.y)}`,
    `${round(baseX - into.y * ARROW.halfWidth)},${round(baseY - into.x * ARROW.halfWidth)}`,
    `${round(baseX + into.y * ARROW.halfWidth)},${round(baseY + into.x * ARROW.halfWidth)}`,
  ].join(' ')

  const label = midpoint(points)
  return { path: polyline(points), arrow, labelX: round(label.x), labelY: round(label.y) }
}

/** Baseline of the trigger row at `index`, inside the triggers box. */
export function triggerRowY(layout: TopologyLayout, index: number): number {
  const { triggers } = layout.boxes
  return triggers.y + layout.metrics.header + layout.metrics.triggerStep * (index + 0.5)
}

/** The machine box at `index`, inside the targets box. */
export function machineRect(layout: TopologyLayout, index: number): Box {
  const { targets } = layout.boxes
  const { header, machineHeight, machineGap, inset } = layout.metrics
  return {
    x: targets.x + inset,
    y: targets.y + header + (machineHeight + machineGap) * index,
    width: targets.width - 2 * inset,
    height: machineHeight,
  }
}

/** The chip for a service part at `index`, on one row inside the service box. */
export function chipRect(layout: TopologyLayout, index: number): Box {
  const { service } = layout.boxes
  const { chipHeight, chipGap, inset } = layout.metrics
  const available = service.width - 2 * inset - chipGap * (SERVICE_PARTS.length - 1)
  const width = available / SERVICE_PARTS.length
  return {
    x: service.x + inset + (width + chipGap) * index,
    y: service.y + service.height - inset - chipHeight,
    width,
    height: chipHeight,
  }
}

/** Baseline of the note under the machines, inside the targets box. */
export function targetsNoteY(layout: TopologyLayout): number {
  const last = machineRect(layout, MACHINE_COUNT - 1)
  return last.y + last.height + 20
}
