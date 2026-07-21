import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';
import { computeDefinitionDiff, diffIsEmpty, type WorkflowDefinition } from '../../lib/workflowDiff';

function StatChip({ label, count, color }: Readonly<{ label: string; count: number; color: string }>) {
  return (
    <div className={`rounded-md px-2 py-1 text-center ${color}`}>
      <div className="text-[9px] font-label font-bold uppercase tracking-widest opacity-80">{label}</div>
      <div className="font-headline text-base font-bold tabular-nums">{count}</div>
    </div>
  );
}

function NodeList({ title, ids, nodes, color }: Readonly<{ title: string; ids: string[]; nodes: Map<string, Node>; color: string }>) {
  if (ids.length === 0) return null;
  return (
    <div>
      <h4 className="mb-0.5 font-label text-[9px] font-bold uppercase tracking-widest text-outline">{title}</h4>
      <ul className="space-y-0.5">
        {ids.map((id) => {
          const n = nodes.get(id);
          const label = (n?.data as Record<string, unknown> | undefined)?.label as string | undefined;
          const type = (n?.data as Record<string, unknown> | undefined)?.activityType as string | undefined;
          return (
            <li key={id} className={`font-label text-[11px] ${color}`}>
              <span className="font-semibold">{label || id}</span>
              {type && <span className="ml-1.5 font-mono text-[9px] text-outline">{type}</span>}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/**
 * A generic, ID-stable diff view of two definitions. Reused by the chat assistant (previewing
 * a proposal) and by the version diff. Comparison is done via {@link computeDefinitionDiff}
 * (keyed on node/edge IDs, including handles/positions).
 */
export function DefinitionDiffViewer({ base, current }: Readonly<{ base: WorkflowDefinition; current: WorkflowDefinition }>) {
  const { t } = useTranslation('editor');
  const diff = useMemo(() => computeDefinitionDiff(base, current), [base, current]);
  const curNodes = useMemo(() => new Map(current.nodes.map((n) => [n.id, n])), [current.nodes]);
  const baseNodes = useMemo(() => new Map(base.nodes.map((n) => [n.id, n])), [base.nodes]);

  if (diffIsEmpty(diff)) {
    return <div className="text-[11px] italic text-outline">{t('diff.noDifferences')}</div>;
  }

  const edgeChanges = diff.addedEdges.length + diff.removedEdges.length + diff.changedEdges.length;

  return (
    <div className="space-y-2">
      <div className="grid grid-cols-3 gap-1.5">
        <StatChip label={t('diff.statAdded')} count={diff.addedNodes.length + diff.addedEdges.length} color="bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300" />
        <StatChip label={t('diff.statRemoved')} count={diff.removedNodes.length + diff.removedEdges.length} color="bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300" />
        <StatChip label={t('diff.statChanged')} count={diff.changedNodes.length + diff.changedEdges.length} color="bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300" />
      </div>
      <NodeList title={t('diff.nodesAdded')} ids={diff.addedNodes} nodes={curNodes} color="text-green-700 dark:text-green-400" />
      <NodeList title={t('diff.nodesRemoved')} ids={diff.removedNodes} nodes={baseNodes} color="text-red-700 dark:text-red-400" />
      <NodeList title={t('diff.nodesChanged')} ids={diff.changedNodes} nodes={curNodes} color="text-amber-700 dark:text-amber-400" />
      {edgeChanges > 0 && (
        <div>
          <h4 className="mb-0.5 font-label text-[9px] font-bold uppercase tracking-widest text-outline">{t('diff.edges')}</h4>
          <ul className="space-y-0.5 font-mono text-[10px]">
            {diff.addedEdges.map((id) => <li key={id} className="text-green-700 dark:text-green-400">+ {edgeLabel(current, id)}</li>)}
            {diff.removedEdges.map((id) => <li key={id} className="text-red-700 dark:text-red-400">− {edgeLabel(base, id)}</li>)}
            {diff.changedEdges.map((id) => <li key={id} className="text-amber-700 dark:text-amber-400">~ {edgeLabel(current, id)}</li>)}
          </ul>
        </div>
      )}
    </div>
  );
}

function edgeLabel(def: WorkflowDefinition, id: string): string {
  const e = def.edges.find((x) => x.id === id);
  return e ? `${e.source} → ${e.target}` : id;
}

export default DefinitionDiffViewer;
