/* Geometry for the workflow example on the home page. Shapes and edges follow the product's
   workflow designer: the silhouettes from nodepilot-ui's designer/nodes/shapes.ts and React Flow's
   bezier edges. */

export const NODE_KEYS = ['start', 'script', 'check', 'result', 'error'] as const
export type NodeKey = (typeof NODE_KEYS)[number]

export type ShapeName = 'pennant' | 'hexPointy' | 'hexFlat' | 'flag' | 'shield'

/** Polygon vertices in percent of the node box, as in the designer. */
export const SHAPES: Record<ShapeName, readonly (readonly [number, number])[]> = {
  pennant: [[100, 0], [25, 0], [0, 50], [25, 100], [100, 100]],
  hexPointy: [[50, 0], [100, 25], [100, 75], [50, 100], [0, 75], [0, 25]],
  hexFlat: [[25, 0], [75, 0], [100, 50], [75, 100], [25, 100], [0, 50]],
  flag: [[0, 0], [75, 0], [100, 50], [75, 100], [0, 100]],
  shield: [[0, 0], [100, 0], [100, 55], [50, 100], [0, 55]],
}

/** Icon offsets as a fraction of the box, so the icon sits on the shape's visual centre. */
export const ICON_OFFSET: Record<ShapeName, readonly [number, number]> = {
  pennant: [0.06, 0],
  hexPointy: [0, 0],
  hexFlat: [0, 0],
  flag: [-0.06, 0],
  shield: [0, -0.1],
}

/** manualTrigger, runScript, serviceManagement, returnData and log in the designer. */
export const NODE_SHAPE: Record<NodeKey, ShapeName> = {
  start: 'pennant',
  script: 'hexPointy',
  check: 'hexFlat',
  result: 'flag',
  error: 'shield',
}

/** Edge id, source node and target node. */
export const EDGES = [
  ['start', 'start', 'script'],
  ['check', 'script', 'check'],
  ['result', 'check', 'result'],
  ['error', 'check', 'error'],
] as const satisfies readonly (readonly [string, NodeKey, NodeKey])[]

export interface NodeLayout {
  x: number
  y: number
  box: number
}

export interface GraphLayout {
  width: number
  height: number
  nodes: Record<NodeKey, NodeLayout>
  /** Minimap frame, or null where the canvas is too small for it. */
  minimap: { x: number; y: number; width: number; height: number } | null
}

export const LAYOUTS: { wide: GraphLayout; compact: GraphLayout } = {
  wide: {
    width: 640,
    height: 315,
    nodes: {
      start: { x: 58, y: 150, box: 56 },
      script: { x: 196, y: 150, box: 52 },
      check: { x: 332, y: 150, box: 52 },
      result: { x: 505, y: 74, box: 56 },
      error: { x: 505, y: 228, box: 54 },
    },
    minimap: { x: 548, y: 253, width: 80, height: 50 },
  },
  compact: {
    width: 420,
    height: 340,
    nodes: {
      start: { x: 48, y: 142, box: 48 },
      script: { x: 148, y: 142, box: 44 },
      check: { x: 248, y: 142, box: 44 },
      result: { x: 362, y: 70, box: 48 },
      error: { x: 362, y: 240, box: 46 },
    },
    minimap: null,
  },
}

/** Arrow length and half width in canvas units. */
const ARROW = { length: 9, halfWidth: 4.5 }

function round(value: number): number {
  return Math.round(value * 10) / 10
}

/**
 * SVG points for a shape centred on 0,0. `inset` shrinks the box on every side, like the
 * designer's stacked clip-path layers (frame, border, fill). A negative inset grows it.
 */
export function shapePoints(shape: ShapeName, box: number, inset = 0): string {
  const size = box - 2 * inset
  const half = size / 2
  return SHAPES[shape]
    .map(([x, y]) => `${round(-half + (x / 100) * size)},${round(-half + (y / 100) * size)}`)
    .join(' ')
}

export interface EdgeGeometry {
  path: string
  arrow: string
  labelX: number
  labelY: number
}

/**
 * Edge from a right-hand port to a left-hand port: a bezier whose control points sit halfway
 * along the horizontal distance, as React Flow draws it, ending in an arrowhead at the target.
 */
export function edgeGeometry(sx: number, sy: number, tx: number, ty: number): EdgeGeometry {
  // The line stops inside the arrowhead so its end does not show past the tip.
  const ex = tx - ARROW.length + 1
  const offset = Math.abs(ex - sx) / 2
  const c1x = sx + offset
  const c2x = ex - offset
  const path = `M${round(sx)} ${round(sy)} C${round(c1x)} ${round(sy)} ${round(c2x)} ${round(ty)} ${round(ex)} ${round(ty)}`
  const baseX = round(tx - ARROW.length)
  const arrow = `${round(tx)},${round(ty)} ${baseX},${round(ty - ARROW.halfWidth)} ${baseX},${round(ty + ARROW.halfWidth)}`
  // Point of the cubic curve at t = 0.5.
  const labelX = 0.125 * sx + 0.375 * c1x + 0.375 * c2x + 0.125 * ex
  const labelY = (sy + ty) / 2
  return { path, arrow, labelX: round(labelX), labelY: round(labelY) }
}

/** Edge geometry between two nodes of a layout, from the right edge of one box to the left edge of the other. */
export function layoutEdge(layout: GraphLayout, from: NodeKey, to: NodeKey): EdgeGeometry {
  const source = layout.nodes[from]
  const target = layout.nodes[to]
  return edgeGeometry(source.x + source.box / 2, source.y, target.x - target.box / 2, target.y)
}
