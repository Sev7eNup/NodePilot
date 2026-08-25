import type { Edge, XYPosition } from '@xyflow/react';
import { getPortPoint, type EdgePortSide } from './edgePorts';

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
 * - `ok`            — umhängen
 * - `cancel`        — der bisherige Ziel-Node: der Nutzer legt die Edge dort wieder ab, wo
 *                     sie herkam. Kein Fehler, keine Mutation, kein History-Eintrag.
 * - `selfLoop`      — der Quell-Node der Edge. Eine Edge auf sich selbst ist im Graphen
 *                     bedeutungslos und ist als Fehlklick weit wahrscheinlicher als Absicht.
 * - `duplicate`     — von derselben Quelle existiert schon eine Edge dorthin. Gleiche Regel
 *                     wie `onConnect`/`onReconnect`: pro Knotenpaar höchstens eine Edge.
 * - `invalidTarget` — Gruppe oder Sticky-Note. Beide haben keine Ausführungssemantik.
 */
export type ReattachVerdict = 'ok' | 'cancel' | 'selfLoop' | 'duplicate' | 'invalidTarget';

export interface ReattachCandidate {
  id: string;
  type?: string;
}

export function classifyReattachTarget({
  edges,
  edgeId,
  sourceId,
  currentTargetId,
  candidate,
}: {
  edges: Edge[];
  edgeId: string;
  sourceId: string;
  currentTargetId: string;
  candidate: ReattachCandidate;
}): ReattachVerdict {
  // Reihenfolge ist bedeutungstragend: „zurück aufs alte Ziel" wird als Abbruch gelesen,
  // bevor irgendeine Ablehnung greifen kann, und der Quell-Node bekommt seine eigene
  // Meldung statt der generischen Typ-Ablehnung.
  if (candidate.id === currentTargetId) return 'cancel';
  if (candidate.id === sourceId) return 'selfLoop';
  if (candidate.type !== 'activity') return 'invalidTarget';
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
