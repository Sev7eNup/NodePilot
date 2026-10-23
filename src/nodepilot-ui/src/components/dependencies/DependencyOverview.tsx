import { ChevronRight } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { dependencyKey, type DependencyDirection, type DependencyItem } from '../../api/dependencies';
import { DependencyIconBadge } from './DependencyObjectIcon';

export function DependencyOverview({ items, selected, direction, onSelect }: {
  items: DependencyItem[]; selected: string | null; direction: DependencyDirection; onSelect: (item: DependencyItem) => void;
}) {
  const { t } = useTranslation('dependencies');
  const groups = [
    { key: 'direct', items: items.filter(i => !i.possible && i.distance === 1) },
    { key: 'indirect', items: items.filter(i => !i.possible && i.distance > 1) },
    { key: 'possible', items: items.filter(i => i.possible) },
  ];
  return <div className="np-dependency-overview">
    {groups.filter(g => g.items.length > 0).map(group => <section className="np-card np-dependency-panel" key={group.key} aria-label={t(`groups.${group.key}`)}>
      <h2 className="np-dependency-section-title">{t(`groups.${group.key}`)} <span className="text-on-surface-variant">· {group.items.length}</span></h2>
      <ul>{group.items.map(item => {
        const leg = item.paths[0]?.links[direction === 'usedBy' ? item.paths[0].links.length - 1 : 0];
        const intermediate = item.paths[0]?.links[direction === 'usedBy' ? 0 : item.paths[0].links.length - 1];
        const location = leg?.locations[0];
        return <li key={dependencyKey(item.object)}>
          <button type="button" className={`np-dependency-row ${selected === dependencyKey(item.object) ? 'is-selected' : ''}`}
            aria-pressed={selected === dependencyKey(item.object)} onClick={() => onSelect(item)}>
            <DependencyIconBadge kind={item.object.kind} />
            <span className="min-w-0 flex-1 text-left">
              <span className="np-dependency-name-line"><span className="np-dependency-name">{item.object.name}</span>{item.object.context && <span className="np-dependency-meta">{item.object.context}</span>}</span>
              <span className="np-dependency-explanation">
                {item.distance > 1 && intermediate
                  ? t('via', { name: direction === 'usedBy' ? intermediate.target.name : intermediate.source.name })
                  : location?.nodeId
                    ? t('stepRelation', { name: location.label, relation: t(`relations.${leg?.relation ?? 'usesMachine'}`) })
                    : t(`relations.${leg?.relation ?? 'usesMachine'}`)}
              </span>
              {(item.object.enabled === false || location?.disabled) && <span className="np-dependency-meta">{t('disabled')}</span>}
            </span>
            <span className="np-dependency-row-action"><span>{t('details')}</span><ChevronRight size={18} /></span>
          </button>
        </li>;
      })}</ul>
    </section>)}
  </div>;
}
