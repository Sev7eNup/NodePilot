import { useState } from 'react';
import { ReactFlow, Background, Controls, Handle, Position, MarkerType, type Node, type NodeProps, type Edge } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { ArrowRight } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { dependencyKey, type DependencyDirection, type DependencyItem, type DependencyObject } from '../../api/dependencies';
import { DependencyIconBadge } from './DependencyObjectIcon';
import { dependencyViewMemory } from '../../hooks/useDependencyView';

const columnWidth = 320, nodeWidth = 252, rowHeight = 88, maxObjects = 40;
const sides: DependencyDirection[] = ['usedBy', 'uses'];

type Expand = { count: number; expanded: boolean; toggle: () => void };
type ObjectNode = Node<{ object: DependencyObject; root: boolean; selected: boolean; select: () => void; expand: Expand | null }, 'resource'>;
type RelationNode = Node<{ label: string }, 'relation'>;
type LinkData = { relation: string; side: DependencyDirection; item: string; column: string };
function ObjectNodeView({ data }: NodeProps<ObjectNode>) {
  const { t } = useTranslation('dependencies');
  return <div className={`np-dependency-map-node ${data.root ? 'is-root' : ''} ${data.selected ? 'is-selected' : ''}`}>
    <Handle type="target" position={Position.Left} isConnectable={false} />
    <button onClick={data.select} aria-pressed={data.selected} disabled={data.root}>
      <DependencyIconBadge kind={data.object.kind} size={16} /><span className="min-w-0"><strong>{data.object.name}</strong><small>{t(`kinds.${data.object.kind}`)}</small></span>
    </button>
    {data.expand && <button className="np-dependency-map-expand" onClick={data.expand.toggle} aria-expanded={data.expand.expanded}
      aria-label={t(data.expand.expanded ? 'mapCollapse' : 'mapExpand', { count: data.expand.count, name: data.object.name })}>
      {data.expand.expanded ? '−' : `+${data.expand.count}`}
    </button>}
    <Handle type="source" position={Position.Right} isConnectable={false} />
  </div>;
}
function RelationNodeView({ data }: NodeProps<RelationNode>) {
  return <span className="np-dependency-map-relation">{data.label}<ArrowRight size={14} aria-hidden="true" /></span>;
}
const nodeTypes = { resource: ObjectNodeView, relation: RelationNodeView };

// Each side is drawn as a tree: an object hangs off the next object on its first explanatory path.
function leg(item: DependencyItem, side: DependencyDirection) {
  const links = item.paths[0]?.links ?? [];
  return side === 'usedBy' ? links[0] : links.at(-1);
}
function parentKey(item: DependencyItem, side: DependencyDirection) {
  const l = leg(item, side);
  return l ? dependencyKey(side === 'usedBy' ? l.target : l.source) : null;
}

export function DependencyMap({ root, items, direction, selected, onSelect }: {
  root: DependencyObject; items: Record<DependencyDirection, DependencyItem[]>; direction: DependencyDirection; selected: string | null;
  onSelect: (item: DependencyItem, side: DependencyDirection) => void;
}) {
  const { t } = useTranslation('dependencies');
  const rootKey = dependencyKey(root);
  const memoryKey = `map:${rootKey}`;
  const [limit, setLimit] = useState(() => dependencyViewMemory.getState().entries.get(memoryKey)?.count ?? 8);
  const [toggles, setToggles] = useState(() => dependencyViewMemory.getState().entries.get(memoryKey)?.expanded ?? {});
  const [savedViewport] = useState(() => dependencyViewMemory.getState().entries.get(memoryKey)?.viewport);

  const byKey = { usedBy: new Map<string, DependencyItem>(), uses: new Map<string, DependencyItem>() };
  const children = { usedBy: new Map<string, DependencyItem[]>(), uses: new Map<string, DependencyItem[]>() };
  for (const side of sides) for (const item of items[side]) {
    byKey[side].set(dependencyKey(item.object), item);
    const parent = parentKey(item, side);
    if (parent) children[side].set(parent, [...children[side].get(parent) ?? [], item]);
  }
  // Ancestors of the selected object stay open unless the user collapsed one of them.
  const chain = new Set<string>();
  for (let current = selected ? byKey[direction].get(selected) : undefined; current;) {
    const parent = parentKey(current, direction);
    if (!parent || parent === rootKey || chain.has(`${direction}|${parent}`)) break;
    chain.add(`${direction}|${parent}`);
    current = byKey[direction].get(parent);
  }
  const isExpanded = (id: string) => toggles[id] ?? chain.has(id);
  function toggle(id: string) {
    const next = { ...toggles, [id]: !isExpanded(id) };
    setToggles(next); dependencyViewMemory.getState().remember(memoryKey, { expanded: next });
  }

  const highlighted = new Set(selected ? [`${direction}|${selected}`, ...chain] : []);
  const nodes: (ObjectNode | RelationNode)[] = [];
  const edges: Edge<LinkData>[] = [];
  const columns = new Map<string, { x: number; relations: Set<string> }>();
  const rows = { usedBy: 0, uses: 0 };
  let total = 1;
  let hiddenDirect = false;
  for (const side of sides) {
    const sign = side === 'uses' ? 1 : -1;
    const placed = new Set<string>();
    const visit = (item: DependencyItem, parentId: string, depth: number): number => {
      const key = dependencyKey(item.object); const id = `${side}|${key}`;
      placed.add(key); total++;
      const kids = (children[side].get(key) ?? []).filter(k => !placed.has(dependencyKey(k.object)));
      const open = kids.length > 0 && isExpanded(id);
      const ys: number[] = [];
      if (open) for (const kid of kids) { if (total >= maxObjects) break; ys.push(visit(kid, id, depth + 1)); }
      // A parent lines up with its first child, so every subtree reads as one block.
      const y = ys[0] ?? rows[side]++;
      nodes.push({ id, type: 'resource', position: { x: sign * depth * columnWidth, y: y * rowHeight },
        data: { object: item.object, root: false, selected: side === direction && key === selected, select: () => onSelect(item, side),
          expand: kids.length > 0 ? { count: kids.length, expanded: open, toggle: () => toggle(id) } : null } });
      const relation = leg(item, side)?.relation ?? 'usesMachine';
      const column = `${side}:${depth}`;
      if (!columns.has(column)) columns.set(column, { x: side === 'usedBy' ? -depth * columnWidth + nodeWidth : (depth - 1) * columnWidth + nodeWidth, relations: new Set() });
      columns.get(column)!.relations.add(relation);
      const on = highlighted.has(id); const color = on ? 'var(--color-primary)' : 'var(--color-outline)';
      edges.push({ id: `${id}>${parentId}`, source: side === 'usedBy' ? id : parentId, target: side === 'usedBy' ? parentId : id,
        type: 'smoothstep', zIndex: on ? 1 : 0, data: { relation, side, item: key, column },
        markerEnd: { type: MarkerType.ArrowClosed, color }, style: { stroke: color, strokeWidth: on ? 2 : 1.25 } });
      return y;
    };
    const direct = items[side].filter(i => parentKey(i, side) === rootKey);
    const selectedTop = side === direction ? direct.find(i => highlighted.has(`${side}|${dependencyKey(i.object)}`)) : undefined;
    const shown = [...direct.slice(0, limit), ...selectedTop && direct.indexOf(selectedTop) >= limit ? [selectedTop] : []];
    hiddenDirect ||= direct.length > shown.length;
    for (const item of shown) { if (total >= maxObjects) break; visit(item, rootKey, 1); }
  }
  nodes.push({ id: rootKey, type: 'resource', position: { x: 0, y: 0 }, data: { object: root, root: true, selected: false, select: () => {}, expand: null } });
  // A column whose edges all share one relation gets a single heading instead of a label per edge.
  for (const [key, column] of columns) {
    if (column.relations.size === 1) nodes.push({ id: `relation:${key}`, type: 'relation', position: { x: column.x, y: -32 },
      data: { label: t(`relations.${[...column.relations][0]}`) }, width: columnWidth - nodeWidth, selectable: false, draggable: false, focusable: false });
  }
  for (const edge of edges) {
    if (columns.get(edge.data!.column)!.relations.size > 1) Object.assign(edge, { label: t(`relations.${edge.data!.relation}`),
      labelStyle: { fill: 'var(--color-on-surface-variant)', fontSize: 12 }, labelBgStyle: { fill: 'var(--color-surface-low)' }, labelBgPadding: [4, 2] });
  }
  const height = Math.min(570, Math.max(320, Math.max(rows.usedBy, rows.uses, 1) * rowHeight + 112));
  return <section className="np-card np-dependency-panel np-dependency-map" aria-label={t('map')}>
    <div className="np-dependency-section-title flex flex-wrap justify-between gap-2"><h2>{t('map')}</h2><span className="np-dependency-meta">{t('mapHint')}</span></div>
    <div className="np-dependency-map-canvas" style={{ height }}>
      <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} nodesDraggable={false} nodesConnectable={false}
        defaultViewport={savedViewport} fitView={!savedViewport} fitViewOptions={{ padding: 0.06, minZoom: 0.75, maxZoom: 1 }}
        minZoom={0.5} maxZoom={1.5} proOptions={{ hideAttribution: true }}
        onMoveEnd={(_, viewport) => dependencyViewMemory.getState().remember(memoryKey, { viewport })}
        onEdgeClick={(_, edge) => {
          const data = edge.data as LinkData | undefined; const item = data && byKey[data.side].get(data.item);
          if (data && item) onSelect(item, data.side);
        }}>
        <Background color="var(--color-outline-variant)" gap={24} /><Controls showInteractive={false} fitViewOptions={{ minZoom: 0.5, maxZoom: 1 }} />
      </ReactFlow>
    </div>
    {(hiddenDirect || total >= maxObjects) && <button className="np-btn m-4" onClick={() => {
      const next = limit + 8; setLimit(next);
      dependencyViewMemory.getState().remember(memoryKey, { count: next });
    }} disabled={total >= maxObjects}>{t(total >= maxObjects ? 'mapLimit' : 'more')}</button>}
  </section>;
}
