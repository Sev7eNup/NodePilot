import { describe, it, expect, vi } from 'vitest';
import { readHandlePoints, resolveDockTarget } from '../../lib/edgeDetach';
import { nearestPortPoint, getPortPoint, type PortPoint } from '../../lib/edgePorts';

/**
 * `resolveDockTarget` answers where a pointer would dock during an edge detach, and is the
 * only resolution path shared by the preview line and the click.
 *
 * Port points come from the measured DOM handles, not from the `width`/`height` of the
 * `.react-flow__node` rectangle: in the classic layout the handles sit on the inner icon box
 * while the outer rectangle also covers the label below it, and `handleInset` pulls single
 * sides further inward per shape.
 *
 * jsdom has no layout and `getBoundingClientRect` returns zeros, so the rects are stubbed
 * per element.
 */

function rect(el: Element, x: number, y: number, w: number, h: number) {
  vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
    x, y, width: w, height: h, left: x, top: y, right: x + w, bottom: y + h,
    toJSON: () => ({}),
  } as DOMRect);
}

interface HandleSpec {
  port: string;
  /** Handle centre in screen coordinates. */
  cx: number;
  cy: number;
  nodeId?: string;
}

const HANDLE_SIZE = 10;

/** Builds a `.react-flow__node` element with stubbed rects for itself and its handles. */
function buildNode(
  nodeId: string,
  outer: { x: number; y: number; w: number; h: number },
  handles: HandleSpec[],
): HTMLElement {
  const node = document.createElement('div');
  node.className = 'react-flow__node';
  node.setAttribute('data-id', nodeId);
  rect(node, outer.x, outer.y, outer.w, outer.h);

  for (const h of handles) {
    const el = document.createElement('div');
    el.className = 'react-flow__handle';
    el.setAttribute('data-handleid', h.port);
    el.setAttribute('data-nodeid', h.nodeId ?? nodeId);
    rect(el, h.cx - HANDLE_SIZE / 2, h.cy - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE);
    node.appendChild(el);
  }
  document.body.appendChild(node);
  return node;
}

const ALWAYS = () => true;
const NEVER = () => false;

/**
 * Classic node: outer rectangle 200x110 at (100, 100). The icon box is only 60x60 and sits at
 * the top centre, while the wider label below it grows the outer rectangle in both directions.
 * The handles sit on the icon box, and the left side is pulled 8 px inward by `handleInset`,
 * as shapes with slanted edges do.
 */
const ICON = { x: 170, y: 100, w: 60, h: 60 };
const CLASSIC_OUTER = { x: 100, y: 100, w: 200, h: 110 };
const CLASSIC_HANDLES: HandleSpec[] = [
  { port: 'top', cx: 200, cy: 100 },
  { port: 'right', cx: 230, cy: 130 },
  { port: 'bottom', cx: 200, cy: 160 },
  { port: 'left', cx: 178, cy: 130 }, // 170 + 8 px handleInset
];

/** The rejected computation: port points from the outer node rectangle instead of the DOM. */
function outerRectangleFormula(): PortPoint[] {
  return (['top', 'right', 'bottom', 'left'] as const).map((port) => {
    const p = getPortPoint(
      { x: CLASSIC_OUTER.x, y: CLASSIC_OUTER.y, w: CLASSIC_OUTER.w, h: CLASSIC_OUTER.h },
      port,
    );
    return { port, x: p.x, y: p.y };
  });
}

describe('readHandlePoints', () => {
  it('returnsTheMeasuredHandleCentres', () => {
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    const points = readHandlePoints(node, 'n1');

    expect(points).toHaveLength(4);
    expect(points.find((p) => p.port === 'left')).toEqual({ port: 'left', x: 178, y: 130 });
    expect(points.find((p) => p.port === 'top')).toEqual({ port: 'top', x: 200, y: 100 });
  });

  it('foreignNodeIdHandles_areIgnored', () => {
    // A nested node element must not contribute handles that belong to another node.
    const node = buildNode('n1', CLASSIC_OUTER, [
      ...CLASSIC_HANDLES,
      { port: 'left', cx: 999, cy: 999, nodeId: 'other' },
    ]);
    const points = readHandlePoints(node, 'n1');

    expect(points).toHaveLength(4);
    expect(points.filter((p) => p.port === 'left')).toEqual([{ port: 'left', x: 178, y: 130 }]);
  });

  it('unknownHandleId_isIgnored', () => {
    const node = buildNode('n1', CLASSIC_OUTER, [
      ...CLASSIC_HANDLES,
      { port: 'diagonal', cx: 500, cy: 500 },
    ]);
    expect(readHandlePoints(node, 'n1').map((p) => p.port).sort())
      .toEqual(['bottom', 'left', 'right', 'top']);
  });
});

describe('resolveDockTarget', () => {
  it('classicNodeWithLabel_picksADifferentPortThanTheOuterRectangleFormula', () => {
    // Click just inside the bottom edge of the icon box (y=160), horizontally centred.
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    const clickX = 200;
    const clickY = 155;

    // Computed from the outer rectangle, the bottom edge sits 50 px lower because of the
    // label, so the click is equally far from 'top' and 'bottom' and resolves to 'top'.
    // Measuring the DOM avoids that.
    expect(nearestPortPoint(outerRectangleFormula(), clickX, clickY)?.port).toBe('top');

    // Measured, it is unambiguously the bottom handle of the icon box, 5 px away.
    const hit = resolveDockTarget(node, clickX, clickY, ALWAYS);
    expect(hit).toEqual({ nodeId: 'n1', port: 'bottom', screenPoint: { x: 200, y: 160 } });
  });

  it('handleInsetSide_isMeasuredNotAssumed', () => {
    // Click at x=174: just left of the inset handle (178) and still inside the icon box
    // (170). The outer rectangle edge would sit at x=100, 74 px away.
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    expect(nearestPortPoint(outerRectangleFormula(), 174, 130)?.port).toBe('top');

    const hit = resolveDockTarget(node, 174, 130, ALWAYS);
    expect(hit?.port).toBe('left');
    expect(hit?.screenPoint).toEqual({ x: 178, y: 130 }); // the inset, measured point
  });

  it('clickOnADescendant_resolvesViaClosestNode', () => {
    // A real click lands on an icon or label inside the node, never on the node element itself.
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    const inner = document.createElement('span');
    rect(inner, ICON.x, ICON.y, ICON.w, ICON.h);
    node.appendChild(inner);

    expect(resolveDockTarget(inner, 200, 102, ALWAYS)?.port).toBe('top');
  });

  it('nonDockableNode_returnsNull', () => {
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    expect(resolveDockTarget(node, 200, 155, NEVER)).toBeNull();
  });

  it('nodeWithoutHandles_returnsNull', () => {
    const node = buildNode('n1', CLASSIC_OUTER, []);
    expect(resolveDockTarget(node, 200, 155, ALWAYS)).toBeNull();
  });

  it('targetOutsideAnyNode_returnsNull', () => {
    const pane = document.createElement('div');
    pane.className = 'react-flow__pane';
    document.body.appendChild(pane);
    expect(resolveDockTarget(pane, 10, 10, ALWAYS)).toBeNull();
  });

  it('nonElementTarget_returnsNull', () => {
    expect(resolveDockTarget(null, 10, 10, ALWAYS)).toBeNull();
    expect(resolveDockTarget(document, 10, 10, ALWAYS)).toBeNull();
  });
});
