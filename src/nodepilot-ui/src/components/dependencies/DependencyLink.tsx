import { ShareKnowledge } from '@carbon/icons-react';
import { Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { dependencyHref, type DependencyKind } from '../../api/dependencies';

export function DependencyLink({ kind, id }: { kind: DependencyKind; id: string }) {
  const { t } = useTranslation('dependencies');
  return <Link to={dependencyHref(kind, id)} title={t('showDependencies')} aria-label={t('showDependencies')}
    className="inline-flex items-center justify-center rounded-md p-2 text-on-surface-variant hover:text-primary focus-visible:outline-2 focus-visible:outline-primary"
    onClick={e => e.stopPropagation()}><ShareKnowledge size={18} /></Link>;
}
