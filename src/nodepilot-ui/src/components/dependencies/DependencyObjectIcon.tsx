import { Apps, FlowModeler, Password, Screen, ValueVariable } from '@carbon/icons-react';
import type { DependencyKind } from '../../api/dependencies';

const icons = { workflow: FlowModeler, machine: Screen, credential: Password, variable: ValueVariable, customActivity: Apps };
// One hue per object kind; green, red and amber stay reserved for status.
const tones: Record<DependencyKind, string> = {
  workflow: 'bg-sky-100 text-sky-700 dark:bg-sky-900/40 dark:text-sky-300',
  machine: 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300',
  credential: 'bg-fuchsia-100 text-fuchsia-700 dark:bg-fuchsia-900/40 dark:text-fuchsia-300',
  variable: 'bg-teal-100 text-teal-700 dark:bg-teal-900/40 dark:text-teal-300',
  customActivity: 'bg-violet-100 text-violet-700 dark:bg-violet-900/40 dark:text-violet-300',
};

export function DependencyObjectIcon({ kind, size = 18 }: { kind: DependencyKind; size?: number }) {
  const Icon = icons[kind];
  return <Icon size={size} aria-hidden="true" />;
}

export function DependencyIconBadge({ kind, size, className = '' }: { kind: DependencyKind; size?: number; className?: string }) {
  return <span className={`np-dependency-icon ${tones[kind]} ${className}`}><DependencyObjectIcon kind={kind} size={size} /></span>;
}
