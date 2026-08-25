import { describe, it, expect, vi } from 'vitest';
import { readHandlePoints, resolveDockTarget } from '../../lib/edgeDetach';
import { nearestPortPoint, getPortPoint, type PortPoint } from '../../lib/edgePorts';

/**
 * `resolveDockTarget` beantwortet für den Edge-Detach die Frage „woran würde dieser Zeiger
 * andocken?" — und ist der EINZIGE Auflösungspfad für Vorschau-Linie und Klick zugleich.
 *
 * Der zentrale Fall unten (`classicNodeWithLabel_…`) pinnt genau den Fehler, an dem ein
 * naiver Ansatz scheitert: die Port-Punkte aus `width`/`height` des `.react-flow__node`-
 * Rechtecks zu rechnen. Im Classic-Layout hängen die Handles am inneren Icon-Kasten, während
 * das äußere Rechteck zusätzlich das Label darunter umfasst — und `handleInset` schiebt
 * einzelne Seiten je nach Shape noch weiter nach innen. Die Außenrechteck-Formel liefert
 * dort nachweislich einen ANDEREN Port als die echten Handles.
 *
 * jsdom kennt kein Layout, `getBoundingClientRect` liefert überall Nullen — die Rects werden
 * deshalb pro Element gestubbt.
 */

function rect(el: Element, x: number, y: number, w: number, h: number) {
  vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
    x, y, width: w, height: h, left: x, top: y, right: x + w, bottom: y + h,
    toJSON: () => ({}),
  } as DOMRect);
}

interface HandleSpec {
  port: string;
  /** Mittelpunkt des Handles in Screen-Koordinaten. */
  cx: number;
  cy: number;
  nodeId?: string;
}

const HANDLE_SIZE = 10;

/** Baut ein `.react-flow__node`-Element mit gestubbten Rects für sich und seine Handles. */
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
 * Classic-Node: äußeres Rechteck 200×110 bei (100, 100) — der Icon-Kasten ist nur 60×60 und
 * sitzt oben mittig, das breitere Label darunter bläht das äußere Rechteck in BEIDE
 * Richtungen auf. Die Handles hängen am ICON-Kasten, und die linke Seite ist per
 * `handleInset` um 8 px nach innen gezogen, wie es Shapes mit schrägen Kanten tun.
 */
const ICON = { x: 170, y: 100, w: 60, h: 60 };
const CLASSIC_OUTER = { x: 100, y: 100, w: 200, h: 110 };
const CLASSIC_HANDLES: HandleSpec[] = [
  { port: 'top', cx: 200, cy: 100 },
  { port: 'right', cx: 230, cy: 130 },
  { port: 'bottom', cx: 200, cy: 160 },
  { port: 'left', cx: 178, cy: 130 }, // 170 + 8 px handleInset
];

/** Die verworfene Rechnung: Port-Punkte aus dem äußeren Node-Rechteck statt aus dem DOM. */
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
    // Ein verschachtelt gerendertes Node-Element darf keine fremden Handles einschleusen.
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
    // Klick knapp unter der Unterkante des ICON-Kastens (y=160), horizontal mittig.
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    const clickX = 200;
    const clickY = 155;

    // Aus dem Außenrechteck gerechnet liegt die Unterkante 50 px tiefer (Label!) — der Klick
    // ist dann von 'top' und 'bottom' gleich weit weg und fällt auf 'top'. Das ist der
    // konkrete Fehlgriff, den die DOM-Messung verhindert.
    expect(nearestPortPoint(outerRectangleFormula(), clickX, clickY)?.port).toBe('top');

    // Gemessen ist es eindeutig das Bottom-Handle des Icon-Kastens, 5 px entfernt.
    const hit = resolveDockTarget(node, clickX, clickY, ALWAYS);
    expect(hit).toEqual({ nodeId: 'n1', port: 'bottom', screenPoint: { x: 200, y: 160 } });
  });

  it('handleInsetSide_isMeasuredNotAssumed', () => {
    // Klick auf x=174: knapp links vom eingerückten Handle (178), noch innerhalb des
    // Icon-Kastens (170). Die Außenrechteck-Kante läge bei x=100 — 74 px daneben.
    const node = buildNode('n1', CLASSIC_OUTER, CLASSIC_HANDLES);
    expect(nearestPortPoint(outerRectangleFormula(), 174, 130)?.port).toBe('top');

    const hit = resolveDockTarget(node, 174, 130, ALWAYS);
    expect(hit?.port).toBe('left');
    expect(hit?.screenPoint).toEqual({ x: 178, y: 130 }); // der eingerückte, echte Punkt
  });

  it('clickOnADescendant_resolvesViaClosestNode', () => {
    // Getroffen wird real ein Icon/Label im Node, nie das Node-Element selbst.
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
