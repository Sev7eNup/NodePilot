import { api } from './client';

export type DependencyKind = 'workflow' | 'machine' | 'credential' | 'variable' | 'customActivity';
export type DependencyDirection = 'uses' | 'usedBy';
export interface DependencyObject {
  kind: DependencyKind; id: string; name: string; context: string; enabled: boolean | null; revision: string;
}
export interface DependencyLocation {
  sourceKind: DependencyKind; sourceId: string; nodeId: string | null; label: string; field: string;
  disabled: boolean; revision: string; viaMachineId: string | null;
}
export interface DependencyLeg {
  source: DependencyObject; target: DependencyObject; relation: string;
  locationCount: number; locations: DependencyLocation[];
}
export interface DependencyPath { links: DependencyLeg[] }
export interface DependencyItem { object: DependencyObject; distance: number; possible: boolean; paths: DependencyPath[] }
export type DependencyObjectState = 'problems' | 'unused';
export type DependencyObjectSort = 'name' | 'usage';
export interface DependencyObjectSummary { object: DependencyObject; usedByDirect: number; usedByIndirect: number; problems: number }
export interface DependencyObjects { items: DependencyObjectSummary[]; analysisRevision: string; nextCursor: string | null }
export interface DependencyReport {
  object: DependencyObject; direction: DependencyDirection; generatedAt: string; analysisRevision: string;
  complete: boolean; limitations: string[]; counts: { direct: number; indirect: number; possible: number };
  items: DependencyItem[]; problems: { kind: string; status: string; location: DependencyLocation }[]; nextCursor: string | null;
}
export interface DependencyEvidence {
  analysisRevision: string; paths: DependencyPath[]; items: DependencyLocation[]; total: number; nextCursor: string | null;
}
export const dependencyKinds: DependencyKind[] = ['workflow', 'machine', 'credential', 'variable', 'customActivity'];
export function isDependencyKind(value: string | null): value is DependencyKind { return dependencyKinds.some(k => k === value); }
export function dependencyKey(object: Pick<DependencyObject, 'kind' | 'id'>) { return `${object.kind}:${object.id}`; }
function query(values: Record<string, string | null | undefined>) {
  const params = new URLSearchParams();
  Object.entries(values).forEach(([key, value]) => { if (value) params.set(key, value); });
  return params.toString();
}
export const dependenciesApi = {
  objects: (q: string, kind: string | null, state: DependencyObjectState | null, sort: DependencyObjectSort, cursor: string | null) =>
    api.get<DependencyObjects>(`/dependencies/objects?${query({ q, kind, state, sort, cursor })}`),
  report: (kind: string, id: string, direction: DependencyDirection, cursor: string | null) =>
    api.get<DependencyReport>(`/dependencies/${encodeURIComponent(kind)}/${encodeURIComponent(id)}?${query({ direction, cursor })}`),
  evidence: (root: DependencyObject, item: DependencyObject, direction: DependencyDirection, cursor: string | null) =>
    api.get<DependencyEvidence>(`/dependencies/${root.kind}/${encodeURIComponent(root.id)}/evidence?${query({ relatedKind: item.kind, relatedId: item.id, direction, cursor })}`),
};
export function dependencyHref(kind: DependencyKind, id: string) {
  return `/dependencies?${query({ kind, id, direction: kind === 'workflow' ? 'uses' : 'usedBy' })}`;
}
export function dependencySourceHref(kind: DependencyKind, id: string, nodeId?: string | null) {
  if (kind === 'workflow') return `/workflows/${encodeURIComponent(id)}${nodeId ? `?focusNode=${encodeURIComponent(nodeId)}` : ''}`;
  return ({ machine: '/machines', credential: '/credentials', variable: '/global-variables', customActivity: '/custom-activities' })[kind];
}
