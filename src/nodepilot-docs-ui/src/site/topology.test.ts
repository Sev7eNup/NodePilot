import { describe, expect, it } from 'vitest'
import siteHtml from './index.html?raw'
import {
  BOX_KEYS,
  LAYOUTS,
  LINKS,
  LINK_KEYS,
  MACHINE_COUNT,
  SERVICE_PARTS,
  TRIGGER_KEYS,
  chipRect,
  linkGeometry,
  machineRect,
  port,
  targetsNoteY,
  triggerRowY,
} from './topology'
import type { Box, TopologyLayout } from './topology'

const LAYOUT_NAMES = ['wide', 'compact'] as const

function corners(box: Box): Array<[number, number]> {
  return [
    [box.x, box.y],
    [box.x + box.width, box.y + box.height],
  ]
}

function overlaps(a: Box, b: Box): boolean {
  return (
    a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height
  )
}

describe('architecture diagram geometry', () => {
  it('puts a port on the named side of a box', () => {
    const box = { x: 10, y: 20, width: 100, height: 40 }
    expect(port(box, 'left')).toEqual({ x: 10, y: 40 })
    expect(port(box, 'right')).toEqual({ x: 110, y: 40 })
    expect(port(box, 'top')).toEqual({ x: 60, y: 20 })
    expect(port(box, 'bottom', 0.25)).toEqual({ x: 35, y: 60 })
  })

  it('draws a straight link where both ports line up', () => {
    const layout = LAYOUTS.wide
    const store = linkGeometry(layout, LINKS.store.wide)
    expect(store.path).toBe('M480 212L480 259')
    // The arrowhead points into the top of the database box.
    expect(store.arrow).toBe('480,268 484.5,259 475.5,259')
  })

  it('bends once where the two sides are perpendicular', () => {
    const live = linkGeometry(LAYOUTS.wide, LINKS.live.wide)
    expect(live.path).toBe('M480 88L480 31L235 31')
    expect(live.arrow).toBe('226,31 235,26.5 235,35.5')
  })

  it('bends twice where both ends leave along the same axis', () => {
    const winrm = linkGeometry(LAYOUTS.wide, LINKS.winrm.wide)
    expect(winrm.path).toBe('M616 150L675.5 150L675.5 193L735 193')
  })

  it('leaves a box on the side the port sits on', () => {
    // The compact SignalR route runs up the left margin, so its first segment must go left.
    const live = linkGeometry(LAYOUTS.compact, LINKS.live.compact)
    expect(live.path).toBe('M44 392L38.1 392L38.1 63')
    expect(live.arrow).toBe('38.1,54 33.6,63 42.6,63')
  })

  it('places every link label on its own line', () => {
    for (const name of LAYOUT_NAMES) {
      for (const key of LINK_KEYS) {
        const { labelX, labelY } = linkGeometry(LAYOUTS[name], LINKS[key][name])
        expect(labelX, `${name} ${key} x`).toBeGreaterThan(0)
        expect(labelX, `${name} ${key} x`).toBeLessThan(LAYOUTS[name].width)
        expect(labelY, `${name} ${key} y`).toBeGreaterThan(0)
        expect(labelY, `${name} ${key} y`).toBeLessThan(LAYOUTS[name].height)
      }
    }
  })

  it('keeps every box inside its canvas', () => {
    for (const name of LAYOUT_NAMES) {
      const layout: TopologyLayout = LAYOUTS[name]
      for (const key of BOX_KEYS) {
        for (const [x, y] of corners(layout.boxes[key])) {
          expect(x, `${name} ${key} x`).toBeGreaterThanOrEqual(0)
          expect(x, `${name} ${key} x`).toBeLessThanOrEqual(layout.width)
          expect(y, `${name} ${key} y`).toBeGreaterThanOrEqual(0)
          expect(y, `${name} ${key} y`).toBeLessThanOrEqual(layout.height)
        }
      }
    }
  })

  it('keeps the boxes clear of each other', () => {
    for (const name of LAYOUT_NAMES) {
      const boxes = LAYOUTS[name].boxes
      for (const [i, key] of BOX_KEYS.entries()) {
        for (const other of BOX_KEYS.slice(i + 1)) {
          expect(overlaps(boxes[key], boxes[other]), `${name} ${key}/${other}`).toBe(false)
        }
      }
    }
  })

  it('routes every link clear of the boxes it does not connect', () => {
    for (const name of LAYOUT_NAMES) {
      const layout = LAYOUTS[name]
      for (const key of LINK_KEYS) {
        const spec = LINKS[key][name]
        const points = [...linkGeometry(layout, spec).path.matchAll(/[ML](-?[\d.]+) (-?[\d.]+)/g)].map(
          (match) => ({ x: Number(match[1]), y: Number(match[2]) }),
        )
        for (const other of BOX_KEYS.filter((box) => box !== spec.from && box !== spec.to)) {
          const box = layout.boxes[other]
          for (const [i, from] of points.slice(0, -1).entries()) {
            const to = points[i + 1]
            const crosses =
              Math.min(from.x, to.x) < box.x + box.width &&
              box.x < Math.max(from.x, to.x) &&
              Math.min(from.y, to.y) < box.y + box.height &&
              box.y < Math.max(from.y, to.y)
            expect(crosses, `${name} ${key} segment ${i} through ${other}`).toBe(false)
          }
        }
      }
    }
  })

  it('keeps trigger rows, machines, chips and the note inside their boxes', () => {
    for (const name of LAYOUT_NAMES) {
      const layout = LAYOUTS[name]
      const { triggers, targets, service } = layout.boxes
      for (let i = 0; i < TRIGGER_KEYS.length; i += 1) {
        const y = triggerRowY(layout, i)
        expect(y, `${name} trigger ${i}`).toBeGreaterThan(triggers.y)
        expect(y, `${name} trigger ${i}`).toBeLessThan(triggers.y + triggers.height)
      }
      for (let i = 0; i < MACHINE_COUNT; i += 1) {
        const machine = machineRect(layout, i)
        expect(machine.x, `${name} machine ${i}`).toBeGreaterThan(targets.x)
        expect(machine.x + machine.width, `${name} machine ${i}`).toBeLessThan(targets.x + targets.width)
        expect(machine.y + machine.height, `${name} machine ${i}`).toBeLessThan(targets.y + targets.height)
      }
      expect(targetsNoteY(layout), name).toBeLessThan(targets.y + targets.height)
      for (let i = 0; i < SERVICE_PARTS.length; i += 1) {
        const chip = chipRect(layout, i)
        expect(chip.x, `${name} chip ${i}`).toBeGreaterThanOrEqual(service.x)
        expect(chip.x + chip.width, `${name} chip ${i}`).toBeLessThanOrEqual(service.x + service.width)
        expect(chip.y + chip.height, `${name} chip ${i}`).toBeLessThan(service.y + service.height)
      }
    }
  })

  it('has markup for every box, link and repeated element', () => {
    for (const key of BOX_KEYS) expect(siteHtml).toContain(`id="arch-${key}"`)
    for (const key of LINK_KEYS) expect(siteHtml).toContain(`id="arch-link-${key}"`)
    for (const key of TRIGGER_KEYS) expect(siteHtml).toContain(`data-trigger="${key}"`)
    for (const part of SERVICE_PARTS) expect(siteHtml).toContain(`data-part="${part}"`)
    expect([...siteHtml.matchAll(/data-machine="/g)]).toHaveLength(MACHINE_COUNT)
  })
})
