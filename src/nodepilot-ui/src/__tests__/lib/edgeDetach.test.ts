import { describe, it, expect } from 'vitest';
import type { Edge } from '@xyflow/react';
import { classifyReattachTarget, detachedSourcePoint } from '../../lib/edgeDetach';

/**
 * Edge-Detach ("Ziel lösen" im Edge-Kontextmenü). Gepinnt wird hier die reine Logik:
 *   - die Klassifikation eines angeklickten Nodes, inklusive ihrer Reihenfolge (der
 *     Quell-Node bekommt seine eigene Meldung statt der generischen Typ-Ablehnung)
 *   - der Ankerpunkt der Vorschau-Linie je Port-Seite
 *
 * Der BISHERIGE Ziel-Node ist ausdrücklich ein gültiges Ziel: dort erneut anzudocken ist der
 * Weg, nur die Port-Seite zu wechseln. Die Duplikat-Regel schließt die bewegte Edge deshalb
 * aus — sonst würde sie sich selbst als Duplikat erkennen, sobald man sie auf ihrem eigenen
 * Ziel neu ablegt. Ob dabei überhaupt etwas passiert (gleicher Node UND gleicher Port =
 * No-Op ohne History-Eintrag), entscheidet der Aufrufer in WorkflowEditorPage.
 */

const EDGES: Edge[] = [
  { id: 'e1', source: 'a', target: 'b' },
  { id: 'e2', source: 'a', target: 'c' },
];

function classify(candidateId: string, type: string | undefined, edges: Edge[] = EDGES) {
  return classifyReattachTarget({
    edges,
    edgeId: 'e1',
    sourceId: 'a',
    candidate: { id: candidateId, type },
  });
}

/** Der Normalfall: ein Activity-Node. */
const activity = (candidateId: string, edges?: Edge[]) => classify(candidateId, 'activity', edges);

describe('classifyReattachTarget', () => {
  it('freeActivityNode_returnsOk', () => {
    expect(activity('d')).toBe('ok');
  });

  it('currentTarget_returnsOk_soThePortCanBeRepicked', () => {
    // Das bisherige Ziel ist kein Sonderfall mehr: nur so lässt sich die Port-Seite wechseln,
    // ohne die Edge samt Bedingung zu löschen und neu zu ziehen. Dass die Edge sich dabei
    // nicht selbst als Duplikat sieht, hängt am `e.id !== edgeId`-Filter.
    expect(activity('b')).toBe('ok');
  });

  it('sourceNode_returnsSelfLoop', () => {
    expect(activity('a')).toBe('selfLoop');
  });

  it('groupNode_returnsInvalidTarget', () => {
    expect(classify('d', 'group')).toBe('invalidTarget');
  });

  it('stickyNoteNode_returnsInvalidTarget', () => {
    expect(classify('d', 'stickyNote')).toBe('invalidTarget');
  });

  it('nodeWithoutType_returnsInvalidTarget', () => {
    expect(classify('d', undefined)).toBe('invalidTarget');
  });

  it('nodeAlreadyReachedFromSameSource_returnsDuplicate', () => {
    expect(activity('c')).toBe('duplicate');
  });

  it('sameTargetFromDifferentSource_returnsOk', () => {
    // Ein anderer Node darf dasselbe Ziel haben — die Regel ist "eine Edge pro Knotenpaar",
    // nicht "ein eingehender Nachbar".
    const edges: Edge[] = [{ id: 'e1', source: 'a', target: 'b' }, { id: 'e2', source: 'x', target: 'c' }];
    expect(activity('c', edges)).toBe('ok');
  });

  it('edgeBeingMoved_isExcludedFromDuplicateCheck', () => {
    // Nur e1 existiert: der Klick auf ein freies Ziel darf nicht an der eigenen Zeile scheitern.
    const edges: Edge[] = [{ id: 'e1', source: 'a', target: 'b' }];
    expect(activity('z', edges)).toBe('ok');
  });

  it('sourceNodeThatIsAlsoAGroup_prefersSelfLoopVerdict', () => {
    // Reihenfolge-Pin: der präzisere Befund gewinnt gegen die generische Typ-Ablehnung.
    expect(classify('a', 'group')).toBe('selfLoop');
  });
});

describe('detachedSourcePoint', () => {
  const node = { measured: { width: 200, height: 80 }, internals: { positionAbsolute: { x: 100, y: 50 } } };

  it('rightPort_returnsMidRightEdge', () => {
    expect(detachedSourcePoint(node, 'right')).toEqual({ x: 300, y: 90 });
  });

  it('leftPort_returnsMidLeftEdge', () => {
    expect(detachedSourcePoint(node, 'left')).toEqual({ x: 100, y: 90 });
  });

  it('topPort_returnsMidTopEdge', () => {
    expect(detachedSourcePoint(node, 'top')).toEqual({ x: 200, y: 50 });
  });

  it('bottomPort_returnsMidBottomEdge', () => {
    expect(detachedSourcePoint(node, 'bottom')).toEqual({ x: 200, y: 130 });
  });

  it('missingNode_returnsNull', () => {
    expect(detachedSourcePoint(null, 'right')).toBeNull();
    expect(detachedSourcePoint(undefined, 'right')).toBeNull();
  });

  it('unmeasuredNode_returnsNull', () => {
    // React Flow vermisst Nodes erst nach dem ersten Layout-Pass. Bis dahin gibt es keinen
    // sinnvollen Anker — der Aufrufer rendert dann einfach nichts, statt auf (0,0) zu zeigen.
    expect(detachedSourcePoint({ measured: {}, internals: { positionAbsolute: { x: 1, y: 2 } } }, 'right')).toBeNull();
  });
});
