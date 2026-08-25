import type { Edge, XYPosition } from '@xyflow/react';
import { getPortPoint, isEdgePortSide, nearestPortPoint, type EdgePortSide, type PortPoint } from './edgePorts';

/**
 * Edge-Detach: „Ziel lösen" im Edge-Kontextmenü hängt das Zielende einer Edge ab; der
 * nächste Klick auf einen Node hängt es dort wieder an. Die Edge selbst wird währenddessen
 * NICHT mutiert — der Detach-Zustand ist rein transient in WorkflowEditorPage. Deshalb ist
 * jeder Abbruchweg (Esc, Pane-Klick, Rechtsklick) trivial korrekt und hinterlässt weder
 * einen History-Eintrag noch ein `isDirty`.
 *
 * Diese Datei hält die beiden Teile, die ohne React Flow testbar sind: die Klassifikation
 * eines Klick-Ziels und die Berechnung des Quell-Endpunkts für die Vorschau-Linie.
 */

/**
 * Ergebnis eines Klicks auf einen Node, während ein Edge-Ende gelöst ist.
 *
 * - `ok`            — anhängen. Schließt den **bisherigen Ziel-Node ausdrücklich ein**: dort
 *                     erneut anzudocken ist der Weg, nur die Port-Seite zu wechseln, ohne die
 *                     Edge zu löschen und neu zu ziehen. Ob sich dabei überhaupt etwas ändert,
 *                     entscheidet der Aufrufer (gleicher Node + gleicher Port = No-Op).
 * - `selfLoop`      — der Quell-Node der Edge. Eine Edge auf sich selbst ist im Graphen
 *                     bedeutungslos und ist als Fehlklick weit wahrscheinlicher als Absicht.
 * - `duplicate`     — von derselben Quelle existiert schon eine ANDERE Edge dorthin. Gleiche
 *                     Regel wie `onConnect`/`onReconnect`: pro Knotenpaar höchstens eine Edge.
 * - `invalidTarget` — Gruppe oder Sticky-Note. Beide haben keine Ausführungssemantik.
 */
export type ReattachVerdict = 'ok' | 'selfLoop' | 'duplicate' | 'invalidTarget';

export interface ReattachCandidate {
  id: string;
  type?: string;
}

export function classifyReattachTarget({
  edges,
  edgeId,
  sourceId,
  candidate,
}: {
  edges: Edge[];
  edgeId: string;
  sourceId: string;
  candidate: ReattachCandidate;
}): ReattachVerdict {
  // Reihenfolge ist bedeutungstragend: der Quell-Node bekommt seine eigene Meldung statt der
  // generischen Typ-Ablehnung.
  if (candidate.id === sourceId) return 'selfLoop';
  if (candidate.type !== 'activity') return 'invalidTarget';
  // `e.id !== edgeId` schließt die bewegte Edge aus — sonst würde sie sich selbst als
  // Duplikat erkennen, sobald man sie auf ihrem eigenen Ziel neu andockt.
  if (edges.some((e) => e.id !== edgeId && e.source === sourceId && e.target === candidate.id)) {
    return 'duplicate';
  }
  return 'ok';
}

/**
 * Minimal-Ausschnitt aus React Flows `InternalNode`. Bewusst strukturell typisiert, damit
 * der Test hier kein komplettes Node-Internals-Objekt bauen muss.
 */
export interface SourceNodeGeometry {
  measured: { width?: number; height?: number };
  internals: { positionAbsolute: XYPosition };
}

/**
 * Ankerpunkt der Vorschau-Linie: die Mitte des Quell-Ports in Flow-Koordinaten. Nutzt
 * dieselbe Port-Geometrie wie der Designer sonst auch (`getPortPoint`), damit die Vorschau
 * exakt dort startet, wo die committete Edge starten wird.
 *
 * Gibt `null` zurück, solange React Flow den Node noch nicht vermessen hat — der Aufrufer
 * rendert dann einfach nichts.
 */
export function detachedSourcePoint(
  node: SourceNodeGeometry | null | undefined,
  side: EdgePortSide,
): XYPosition | null {
  if (!node) return null;
  const w = node.measured.width;
  const h = node.measured.height;
  if (!w || !h) return null;
  const { x, y } = node.internals.positionAbsolute;
  return getPortPoint({ x, y, w, h }, side);
}

/**
 * Die vier Port-Mittelpunkte eines Nodes in **Screen**-Koordinaten, direkt aus dem DOM.
 *
 * Bewusst gemessen statt gerechnet: das äußere `.react-flow__node`-Rechteck ist nicht die
 * Port-Geometrie. Die Handles hängen im inneren Shape-/Icon-Wrapper (das äußere Rechteck
 * umfasst zusätzlich das Label darunter), und `portHandleStyle` schiebt einzelne Seiten je
 * nach Shape über `handleInset` weiter nach innen. Eine Rechnung aus `width`/`height` würde
 * deshalb Punkte liefern, die sichtbar neben den echten Handles liegen — und damit den
 * falschen Port gewinnen lassen. `getBoundingClientRect` ist zugleich zoom-korrekt.
 *
 * React Flow schreibt an jedes Handle `data-handleid` (bei uns die Port-Seite, siehe die
 * `id={side}`-Handles in ActivityNode) und `data-nodeid`. Über `data-nodeid` wird gefiltert,
 * damit ein verschachtelt gerendertes Node-Element keine fremden Handles einschleust.
 */
export function readHandlePoints(nodeEl: Element, nodeId: string): PortPoint[] {
  const points: PortPoint[] = [];
  for (const handle of nodeEl.querySelectorAll('.react-flow__handle')) {
    if (handle.getAttribute('data-nodeid') !== nodeId) continue;
    const port = handle.getAttribute('data-handleid');
    if (!isEdgePortSide(port)) continue;
    const r = handle.getBoundingClientRect();
    points.push({ port, x: r.left + r.width / 2, y: r.top + r.height / 2 });
  }
  return points;
}

export interface DockTarget {
  nodeId: string;
  port: EdgePortSide;
  /** Mittelpunkt des gewählten Handles in Screen-Koordinaten. */
  screenPoint: { x: number; y: number };
}

/**
 * Wohin würde ein Zeiger an `(clientX, clientY)` andocken?
 *
 * Einziger Auflösungspfad für **beide** Seiten des Edge-Detach — die Vorschau-Linie im
 * `pointermove` und das tatsächliche Umhängen im Klick. Nur weil beide dieselbe Funktion
 * fragen, kann die Vorschau nicht etwas anderes zeigen, als der Klick dann erzeugt.
 *
 * `canDockTo` hält Nodes heraus, die als Ziel ohnehin abgelehnt würden (Quell-Node,
 * Duplikat, Gruppe/Sticky-Note) — dort darf weder die Linie andocken noch der Hover-Ring
 * erscheinen.
 */
export function resolveDockTarget(
  target: EventTarget | null,
  clientX: number,
  clientY: number,
  canDockTo: (nodeId: string) => boolean,
): DockTarget | null {
  if (!(target instanceof Element)) return null;
  const nodeEl = target.closest('.react-flow__node');
  const nodeId = nodeEl?.getAttribute('data-id');
  if (!nodeEl || !nodeId || !canDockTo(nodeId)) return null;
  const nearest = nearestPortPoint(readHandlePoints(nodeEl, nodeId), clientX, clientY);
  if (!nearest) return null;
  return { nodeId, port: nearest.port, screenPoint: { x: nearest.x, y: nearest.y } };
}
