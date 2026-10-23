import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { keepPreviousData, useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, Close, Information, Renew, Search, ShareKnowledge, WarningAlt } from '@carbon/icons-react';
import { Link, useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { dependenciesApi, dependencyKinds, dependencyKey, dependencySourceHref, isDependencyKind, type DependencyDirection, type DependencyItem, type DependencyObject, type DependencyObjectSummary } from '../api/dependencies';
import { DependencyIconBadge, DependencyObjectIcon } from '../components/dependencies/DependencyObjectIcon';
import { DependencyOverview } from '../components/dependencies/DependencyOverview';
import { DependencyDetails } from '../components/dependencies/DependencyDetails';
import { useRole } from '../lib/rbac';
import { formatTime } from '../lib/format';
import { useDependencyView } from '../hooks/useDependencyView';
import '../styles/dependencies.css';

const DependencyMap = lazy(() => import('../components/dependencies/DependencyMap').then(m => ({ default: m.DependencyMap })));

export function DependenciesPage() {
  const { t } = useTranslation('dependencies');
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const { role } = useRole();
  const overviewRef = useRef<HTMLDivElement>(null);
  const kindParam = params.get('kind'); const kind = isDependencyKind(kindParam) ? kindParam : null;
  const id = params.get('id'); const q = params.get('q') ?? ''; const filter = params.get('filter');
  const stateParam = params.get('state'); const state = stateParam === 'problems' || stateParam === 'unused' ? stateParam : null;
  const sort = params.get('sort') === 'usage' ? 'usage' : 'name';
  const direction: DependencyDirection = params.get('direction') === 'uses' ? 'uses' : 'usedBy';
  const opposite: DependencyDirection = direction === 'uses' ? 'usedBy' : 'uses';
  const map = params.get('view') === 'map';
  const selected = params.get('related');
  const visibleKinds = dependencyKinds.filter(k => role !== 'Viewer' || k !== 'credential' && k !== 'variable');
  // The field runs ahead of the URL while typing; a URL change from elsewhere (back, reset) wins.
  const [draft, setDraft] = useState(q);
  const [syncedQ, setSyncedQ] = useState(q);
  if (q !== syncedQ) { setSyncedQ(q); if (draft.trim() !== q) setDraft(q); }
  const searchTimer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(searchTimer.current), []);
  const objects = useInfiniteQuery({
    queryKey: ['dependencies', 'objects', q, filter, state, sort], initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => dependenciesApi.objects(q, filter, state, sort, pageParam), getNextPageParam: p => p.nextCursor,
    enabled: !id, refetchOnWindowFocus: true, retry: false, placeholderData: keepPreviousData,
  });
  const report = useInfiniteQuery({
    queryKey: ['dependencies', 'report', kind, id, direction], initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => dependenciesApi.report(kind ?? 'workflow', id ?? '', direction, pageParam),
    getNextPageParam: p => p.nextCursor, enabled: !!kind && !!id, refetchOnWindowFocus: true, retry: false,
  });
  // The map shows both directions; the opposite report shares its cache entry with the direction switch.
  const oppositeReport = useInfiniteQuery({
    queryKey: ['dependencies', 'report', kind, id, opposite], initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => dependenciesApi.report(kind ?? 'workflow', id ?? '', opposite, pageParam),
    getNextPageParam: p => p.nextCursor, enabled: !!kind && !!id && map, refetchOnWindowFocus: true, retry: false,
  });
  const revoked = report.error instanceof ApiError && [403, 404].includes(report.error.status);
  const data = revoked ? undefined : report.data?.pages[0];
  const items = revoked ? [] : report.data?.pages.flatMap(p => p.items) ?? [];
  const oppositeItems = revoked || oppositeReport.data?.pages[0]?.analysisRevision !== data?.analysisRevision
    ? [] : oppositeReport.data?.pages.flatMap(p => p.items) ?? [];
  const item = items.find(i => dependencyKey(i.object) === selected);
  const pageRef = useDependencyView(id ? !!data : !!objects.data);
  function patch(values: Record<string, string | null>, replace = false) {
    setParams(prev => {
      const next = new URLSearchParams(prev); Object.entries(values).forEach(([key, value]) => value === null ? next.delete(key) : next.set(key, value)); return next;
    }, { replace });
  }
  function search(value: string, delay: number) {
    window.clearTimeout(searchTimer.current);
    // Typing replaces the history entry; leaving an open object keeps it reachable with Back.
    const apply = () => { if (value.trim() !== q || id) patch({ q: value.trim() || null, id: null, kind: null, related: null }, !id); };
    if (delay === 0) apply(); else searchTimer.current = window.setTimeout(apply, delay);
  }
  function open(object: DependencyObject) {
    patch({ kind: object.kind, id: object.id, direction: object.kind === 'workflow' ? 'uses' : 'usedBy', related: null });
  }
  function select(item: DependencyItem, side: DependencyDirection = direction) { patch({ direction: side, related: dependencyKey(item.object) }); }
  function closeDetails() {
    patch({ related: null });
    overviewRef.current?.querySelector<HTMLButtonElement>('[aria-pressed="true"]')?.focus();
  }
  function refresh() { void queryClient.resetQueries({ queryKey: ['dependencies'] }); }
  const error = id ? report.error : objects.error;
  return <div className="np-dependencies" ref={pageRef}>
    <header className="np-dependencies-header"><p>{t('subtitle')}</p>
      <button className="np-btn" onClick={refresh} disabled={report.isFetching || objects.isFetching}><Renew size={16} />{t('refresh')}</button>
    </header>
    <section className="np-card np-dependency-search" aria-label={t('searchLabel')}>
      <form onSubmit={event => { event.preventDefault(); search(draft, 0); }} className="np-dependency-search-field"><Search size={14} aria-hidden="true" />
        <input name="q" value={draft} onChange={event => { setDraft(event.target.value); search(event.target.value, 300); }}
          maxLength={200} aria-label={t('searchLabel')} placeholder={t('searchPlaceholder')} />
        <button type="submit" className="np-btn np-btn-sm np-btn-ghost">{t('search')}</button>
      </form>
      <div className="np-dependency-filters"><button className={`np-btn np-btn-sm ${!filter ? 'np-btn-primary' : 'np-btn-ghost'}`} aria-pressed={!filter}
        onClick={() => patch({ filter: null, id: null, kind: null, related: null })}>{t('all')}</button>
        {visibleKinds.map(k => <button key={k} className={`np-btn np-btn-sm ${filter === k ? 'np-btn-primary' : 'np-btn-ghost'}`} aria-pressed={filter === k}
          onClick={() => patch({ filter: k, id: null, kind: null, related: null })}><DependencyObjectIcon kind={k} size={14} />{t(`kindsPlural.${k}`)}</button>)}
      </div>
    </section>
    {error && <div role="alert" className="np-dependency-notice"><WarningAlt size={18} /><span>{t(revoked ? 'unavailable' : error instanceof ApiError && error.status === 409 ? 'changed' : 'error')}</span><button onClick={refresh} className="np-btn">{t('retry')}</button></div>}
    {(id ? report.isPending && report.fetchStatus !== 'idle' : objects.isPending) && <div role="status" className="np-card np-dependency-panel np-dependency-loading"><span>{t('loading')}</span><div /><div /><div /></div>}
    {!id && objects.data && <section className="np-card np-dependency-panel" aria-label={t('choose')}>
      <div className="np-dependency-section-title np-dependency-list-head">
        <div className="min-w-0"><h2>{t('choose')}</h2><p className="np-dependency-meta" aria-live="polite">{t(state === 'problems' ? 'problemsHint' : state === 'unused' ? 'unusedHint' : 'chooseHint')}</p></div>
        <div className="np-dependency-list-controls">
          <div className="np-segmented" aria-label={t('stateLabel')}>{([null, 'problems', 'unused'] as const).map(value =>
            <button key={value ?? 'all'} className={`np-segment ${state === value ? 'is-active' : ''}`} aria-pressed={state === value}
              onClick={() => patch({ state: value })}>{t(value ? `states.${value}` : 'all')}</button>)}</div>
          <div className="np-segmented" aria-label={t('sort')}>{(['name', 'usage'] as const).map(value =>
            <button key={value} className={`np-segment ${sort === value ? 'is-active' : ''}`} aria-pressed={sort === value}
              onClick={() => patch({ sort: value === 'name' ? null : value })}>{t(`sorts.${value}`)}</button>)}</div>
        </div>
      </div>
      {objects.data.pages.flatMap(p => p.items).map(summary => <ObjectRow key={dependencyKey(summary.object)} summary={summary} onOpen={open} />)}
      {objects.data.pages[0].items.length === 0 && <div className="np-dependency-empty"><Search size={30} /><p>{t('noObjects')}</p><button className="np-btn" onClick={() => patch({ q: null, filter: null, state: null })}>{t('resetFilters')}</button></div>}
      {objects.hasNextPage && <button className="np-btn m-4" disabled={objects.isFetchingNextPage} onClick={() => void objects.fetchNextPage()}>{t('more')}</button>}
    </section>}
    {data && <>
      <section className="np-card np-dependency-panel np-dependency-selection">
        <DependencyIconBadge kind={data.object.kind} size={24} className="np-dependency-hero-icon" />
        <div className="min-w-0 flex-1"><h2>{data.object.name}</h2><p className="text-sm text-on-surface-variant">{t(`kinds.${data.object.kind}`)}{data.object.context && ` · ${data.object.context}`}{data.object.enabled === false && ` · ${t('disabled')}`}</p>
          <p className="np-dependency-summary" aria-live="polite">{!data.complete && `${t('atLeast')} `}{t(direction === 'uses' ? 'summaryUses' : 'summaryUsedBy', data.counts)}{data.counts.possible > 0 && ` ${t('summaryPossible', { count: data.counts.possible })}`}</p>
          <p className="np-dependency-meta">{t('savedScope')} · {formatTime(data.generatedAt, { hour: '2-digit', minute: '2-digit' })}{report.isFetching && ` · ${t('updating')}`}</p>
        </div>
        <button className="np-btn np-btn-icon np-btn-ghost" aria-label={t('clearSelection')} onClick={() => patch({ id: null, kind: null, related: null })}><Close size={16} /></button>
      </section>
      {!data.complete && <p role="status" className="np-dependency-notice"><WarningAlt size={20} />{t('limited')}</p>}
      <div className="np-dependency-switches">
        <div className="np-segmented" aria-label={t('direction')}><button className={`np-segment ${direction === 'usedBy' ? 'is-active' : ''}`} aria-pressed={direction === 'usedBy'} onClick={() => patch({ direction: 'usedBy', related: null })}>{t('usedBy')}</button><button className={`np-segment ${direction === 'uses' ? 'is-active' : ''}`} aria-pressed={direction === 'uses'} onClick={() => patch({ direction: 'uses', related: null })}>{t('uses')}</button></div>
        <div className="np-segmented" aria-label={t('view')}><button className={`np-segment ${!map ? 'is-active' : ''}`} aria-pressed={!map} onClick={() => patch({ view: 'overview' })}>{t('overview')}</button><button className={`np-segment ${map ? 'is-active' : ''}`} aria-pressed={map} onClick={() => patch({ view: 'map' })}>{t('map')}</button></div>
      </div>
      <div className={`np-dependency-body ${item ? 'has-details' : ''}`} ref={overviewRef}>
        <div className="min-w-0">
          {map && (items.length > 0 || oppositeItems.length > 0)
            ? <Suspense fallback={<p role="status">{t('loading')}</p>}><DependencyMap key={id} root={data.object} direction={direction} selected={selected} onSelect={select}
              items={direction === 'usedBy' ? { usedBy: items, uses: oppositeItems } : { usedBy: oppositeItems, uses: items }} /></Suspense>
            : !map && items.length > 0 ? <DependencyOverview items={items} selected={selected} direction={direction} onSelect={select} />
              : <div className="np-card np-dependency-panel np-dependency-empty"><ShareKnowledge size={34} /><p>{t('noRelationships')}</p></div>}
          {report.hasNextPage && <button className="np-btn mt-4" disabled={report.isFetchingNextPage} onClick={() => void report.fetchNextPage()}>{t('more')}</button>}
        </div>
        {item && <DependencyDetails key={`${id}:${direction}:${selected}:${data.analysisRevision}`} root={data.object} item={item} direction={direction} revision={data.analysisRevision} onClose={closeDetails} onCenter={open} />}
      </div>
      {data.problems.length > 0 && <section className="np-card np-dependency-panel np-dependency-problems"><h2 className="np-dependency-section-title">{t('notes')}</h2>
        <ul>{data.problems.map((problem, index) => <li key={index}><Information size={18} /><div><strong>{problem.location.label || t('configuration')}</strong><p>{t(`statuses.${problem.status}`)} · {t(`kinds.${problem.kind}`)}</p>
          <Link className="np-btn np-btn-sm np-dependency-open" to={dependencySourceHref(problem.location.sourceKind, problem.location.sourceId, problem.location.nodeId)} state={{ fromDependencies: true }}>{t(problem.location.nodeId ? 'openStep' : 'openObject')}</Link>
        </div></li>)}</ul>
      </section>}
      {data.limitations.filter(l => l !== 'staticOnly').length > 0 && <div className="np-dependency-notice"><Information size={18} /><p>{data.limitations.filter(l => l !== 'staticOnly').map(l => t(`limitations.${l}`)).join(' ')}</p></div>}
    </>}
    <p className="np-dependency-scope"><Information size={18} /><span>{t('scope')}</span></p>
  </div>;
}

function ObjectRow({ summary, onOpen }: { summary: DependencyObjectSummary; onOpen: (object: DependencyObject) => void }) {
  const { t } = useTranslation('dependencies');
  const { object, usedByDirect, usedByIndirect, problems } = summary;
  return <button className="np-dependency-row" onClick={() => onOpen(object)}>
    <DependencyIconBadge kind={object.kind} />
    <span className="text-left flex-1 min-w-0"><strong className="np-dependency-name">{object.name}</strong><span className="np-dependency-explanation">{t(`kinds.${object.kind}`)}{object.context && ` · ${object.context}`}</span></span>
    <span className="np-dependency-row-facts">
      {problems > 0 && <span className="np-dependency-problem-badge"><WarningAlt size={14} aria-hidden="true" />{t('problemCount', { count: problems })}</span>}
      <span className="np-dependency-usage">{usedByDirect + usedByIndirect > 0 ? t('usageCounts', { direct: usedByDirect, indirect: usedByIndirect }) : t('noUsage')}</span>
    </span>
    <span className="np-dependency-row-action"><ChevronRight size={18} /></span>
  </button>;
}
