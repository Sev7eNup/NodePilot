import { describe, expect, it } from 'vitest'
import siteHtml from './index.html?raw'
import { EDGES, LAYOUTS, NODE_KEYS, NODE_SHAPE, SHAPES, edgeGeometry, layoutEdge, shapePoints } from './graph'

function parsePoints(points: string): Array<[number, number]> {
  return points.split(' ').map((pair) => pair.split(',').map(Number) as [number, number])
}

describe('workflow example geometry', () => {
  it('scales a shape into a box centred on the origin', () => {
    expect(shapePoints('hexFlat', 40)).toBe('-10,-20 10,-20 20,0 10,20 -10,20 -20,0')
    expect(shapePoints('flag', 40, 5)).toBe('-15,-15 7.5,-15 15,0 7.5,15 -15,15')
    expect(shapePoints('shield', 20, -2)).toBe('-12,-12 12,-12 12,1.2 0,12 -12,1.2')
  })

  it('keeps every shape on the vertical middle of both box edges, where edges dock', () => {
    for (const [name, vertices] of Object.entries(SHAPES)) {
      const touchesAt = (edgeX: number) =>
        vertices.some(([x, y], i) => {
          const [nx, ny] = vertices[(i + 1) % vertices.length]
          return x === edgeX && nx === edgeX ? Math.min(y, ny) <= 50 && Math.max(y, ny) >= 50 : x === edgeX && y === 50
        })
      expect(touchesAt(0), `${name} left`).toBe(true)
      expect(touchesAt(100), `${name} right`).toBe(true)
    }
  })

  it('draws a bezier edge that ends in an arrowhead at the target port', () => {
    const edge = edgeGeometry(100, 50, 200, 50)
    expect(edge.path).toBe('M100 50 C146 50 146 50 192 50')
    expect(edge.arrow).toBe('200,50 191,45.5 191,54.5')
    expect(edge.labelX).toBe(146)
    expect(edge.labelY).toBe(50)
  })

  it('puts the label of a bending edge between both ports', () => {
    const edge = edgeGeometry(0, 100, 100, 0)
    expect(edge.labelY).toBe(50)
    expect(edge.labelX).toBeGreaterThan(0)
    expect(edge.labelX).toBeLessThan(100)
  })

  it('fits every node, label and control inside its canvas', () => {
    for (const [name, layout] of Object.entries(LAYOUTS)) {
      for (const key of NODE_KEYS) {
        const { x, y, box } = layout.nodes[key]
        const points = parsePoints(shapePoints(NODE_SHAPE[key], box, -3))
        for (const [px, py] of points) {
          expect(x + px, `${name} ${key}`).toBeGreaterThan(0)
          expect(y + py, `${name} ${key}`).toBeGreaterThan(0)
        }
        // Label and health dots sit below the shape.
        expect(y + box / 2 + 34, `${name} ${key} label`).toBeLessThan(layout.height)
      }
      expect(layout.controls.y + 4 * 22, `${name} controls`).toBeLessThan(layout.height)
      if (layout.minimap) expect(layout.minimap.x + layout.minimap.width).toBeLessThan(layout.width)
    }
  })

  it('connects edges from left to right', () => {
    for (const layout of Object.values(LAYOUTS)) {
      for (const [, from, to] of EDGES) {
        const edge = layoutEdge(layout, from, to)
        const start = Number(edge.path.split(' ')[0].slice(1))
        expect(start).toBeLessThan(layout.nodes[to].x - layout.nodes[to].box / 2)
      }
    }
  })

  it('has markup for every node and edge the geometry lays out', () => {
    for (const key of NODE_KEYS) expect(siteHtml).toContain(`id="node-${key}"`)
    for (const [id] of EDGES) expect(siteHtml).toContain(`id="edge-${id}"`)
  })
})
