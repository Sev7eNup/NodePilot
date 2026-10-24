import { useEffect, useRef } from 'react';
import { useInfiniteQuery } from '@tanstack/react-query';
import { Close, Launch, ShareKnowledge } from '@carbon/icons-react';
import { Link, useLocation } from 'react-router';
import { useTranslation } from 'react-i18next';
import { dependenciesApi, dependencyKey, dependencySourceHref, type DependencyDirection, type DependencyItem, type DependencyObject } from '../../api/dependencies';
import { DependencyIconBadge } from './DependencyObjectIcon';
import { dependencyViewMemory } from '../../hooks/useDependencyView';

export function DependencyDetails({ root, item, direction, revision, onClose, onCenter }: {
  root: DependencyObject; item: DependencyItem; direction: DependencyDirection; revision: string;
  onClose: () => void; onCenter: (object: DependencyObject) => void;
}) {
  const { t } = useTranslation('dependencies');
  const { key: locationKey } = useLocation();
  const panel = useRef<HTMLElement>(null);
  useEffect(() => {
    panel.current?.focus({ preventScroll: true });
    if (window.innerWidth <= 1280 && !dependencyViewMemory.getState().entries.has(locationKey)) panel.current?.scrollIntoView({ block: 'nearest' });
  }, [locationKey]);
  const evidence = useInfiniteQuery({
    queryKey: ['dependencies', 'evidence', root.kind, root.id, item.object.kind, item.object.id, direction, revision],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => dependenciesApi.evidence(root, item.object, direction, pageParam),
    getNextPageParam: page => page.nextCursor,
    refetchOnWindowFocus: true, retry: false,
  });
  const changed = evidence.data?.pages.some(p => p.analysisRevision !== revision);
  const paths = item.paths;
  const locations = changed || evidence.isError ? [] : evidence.data?.pages.flatMap(p => p.items) ?? [];
  const path = paths[0];
  // Evidence carries only source ids; the path objects supply their display names.
  const names = new Map([...paths, ...evidence.data?.pages.flatMap(p => p.paths) ?? []]
    .flatMap(p => p.links.flatMap(l => [l.source, l.target])).map(o => [o.id, o.name]));
  return <aside ref={panel} tabIndex={-1} className="np-card np-dependency-panel np-dependency-details" aria-label={t('why')} onKeyDown={event => {
    if (event.key === 'Escape') { event.stopPropagation(); onClose(); }
  }}>
    <div className="np-dependency-section-title flex items-center justify-between gap-2">
      <h2>{t('why')}</h2><button className="np-btn np-btn-icon-sm np-btn-ghost" onClick={onClose} aria-label={t('close')}><Close size={16} /></button>
    </div>
    <div className="np-dependency-detail-body">
      <p className="text-sm text-on-surface-variant break-words">{path?.links.map(leg =>
        t('sentence', { source: leg.source.name, relation: t(`relations.${leg.relation}`), target: leg.target.name })).join(' ')}</p>
      {path && <ol className="np-dependency-chain" aria-label={t('relationshipPath')}>
        {[path.links[0]?.source, ...path.links.map(l => l.target)].filter((o): o is DependencyObject => !!o).map((object, index) =>
          <li key={`${object.kind}:${object.id}:${index}`}>
            {index > 0 && <div className="np-dependency-chain-connector">{t(`relations.${path.links[index - 1].relation}`)}</div>}
            <div className={`np-dependency-chain-node ${dependencyKey(object) === dependencyKey(root) ? 'is-root' : ''}`}>
              <DependencyIconBadge kind={object.kind} size={16} />
              <span className="min-w-0"><strong>{object.name}</strong><small>{t(`kinds.${object.kind}`)}</small></span>
            </div>
          </li>)}
      </ol>}
      {paths.length > 1 && <details className="np-dependency-alternative" data-dependency-section="alternatives"><summary>{t('alternativePaths', { count: paths.length - 1 })}</summary>
        {paths.slice(1).map((p, i) => <p key={i} className="mt-3 text-sm">{p.links.map(leg => t('sentence', { source: leg.source.name, relation: t(`relations.${leg.relation}`), target: leg.target.name })).join(' ')}</p>)}
      </details>}
      <div className="np-dependency-evidence"><h4 className="font-semibold">{t('evidence')}</h4>
        {evidence.isPending && <p role="status">{t('loading')}</p>}
        {(evidence.isError || changed) && <p role="alert" className="text-sm text-on-surface-variant">{t(changed ? 'changed' : 'evidenceError')}</p>}
        {locations.map((location, index) => <div key={`${location.sourceId}:${location.nodeId}:${location.field}:${index}`} className="np-dependency-evidence-item">
          <p className="text-sm font-medium break-words">{location.label || t('configuration')}{location.disabled && <span className="text-on-surface-variant"> · {t('disabled')}</span>}</p>
          {names.has(location.sourceId) && <p className="np-dependency-meta">{t('inSource', { name: names.get(location.sourceId) })}</p>}
          {location.viaMachineId && <p className="np-dependency-meta">{t('inherited')}</p>}
          <Link className="np-btn np-btn-sm np-dependency-open" to={dependencySourceHref(location.sourceKind, location.sourceId, location.nodeId)} state={{ fromDependencies: true }}>
            <Launch size={14} /><span>{t(location.nodeId ? 'openStep' : 'openObject')}</span>
          </Link>
          <details className="np-dependency-tech" data-dependency-section={`${location.sourceId}:${location.nodeId}:${location.field}`}><summary>{t('technicalLocation')}</summary><code>{location.field}</code></details>
        </div>)}
        {evidence.hasNextPage && <button className="np-btn" disabled={evidence.isFetchingNextPage} onClick={() => void evidence.fetchNextPage()}>{t('moreEvidence')}</button>}
      </div>
      <p className="np-dependency-meta mb-4">{t('pathsHint')}</p>
      <button className="np-btn w-full" onClick={() => onCenter(item.object)}><ShareKnowledge size={16} />{t('center')}</button>
    </div>
  </aside>;
}
