import { CheckmarkFilled, ChevronRight, Copy, DataBase } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { DatabusEntry, StepUpdate } from '../../../hooks/useSignalR';
import { StepStatusIcon } from './ExecutionPanelParts';

export function OutputTab({ databus, steps }: Readonly<{ databus: Record<string, DatabusEntry>; steps: StepUpdate[] }>) {
  const { t } = useTranslation('designer');
  const [filter, setFilter] = useState('');
  const [expandedKeys, setExpandedKeys] = useState<Set<string>>(new Set());
  // Per-activity (group) collapse — all groups start collapsed; user clicks the header
  // to reveal entries. Filter typing auto-expands matching groups (handled below).
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [copied, setCopied] = useState<string | null>(null);

  // Only show structured named variables — raw .output/.error text blobs are excluded.
  const allEntries = Object.entries(databus).filter(([, e]) => e.kind !== 'output' && e.kind !== 'error');

  const filtered = filter.length > 0
    ? allEntries.filter(([k, e]) =>
        k.toLowerCase().includes(filter.toLowerCase()) ||
        e.value.toLowerCase().includes(filter.toLowerCase()))
    : allEntries;

  const toggle = (key: string) => setExpandedKeys((prev) => {
    const next = new Set(prev);
    if (next.has(key)) next.delete(key); else next.add(key);
    return next;
  });

  const toggleGroup = (groupKey: string) => setExpandedGroups((prev) => {
    const next = new Set(prev);
    if (next.has(groupKey)) next.delete(groupKey); else next.add(groupKey);
    return next;
  });

  const copy = (key: string, value: string) => {
    navigator.clipboard.writeText(value).catch(() => {});
    setCopied(key);
    setTimeout(() => setCopied(null), 1500);
  };

  if (allEntries.length === 0) {
    const isRunning = steps.length > 0;
    return (
      <div className="flex flex-col items-center justify-center h-full gap-2 text-on-surface-variant">
        <DataBase size={24} className="text-outline-variant" />
        <span className="font-label text-sm">
          {isRunning ? t('execution.output.waitingNamedParams') : t('execution.output.noDataBusValues')}
        </span>
        <span className="font-label text-xs text-outline">
          {isRunning
            ? t('execution.output.noEntriesRunning')
            : t('execution.output.noEntriesIdle')}
        </span>
      </div>
    );
  }

  const triggerEntries = filtered.filter(([, e]) => e.kind === 'trigger');
  const globalEntries  = filtered.filter(([, e]) => e.kind === 'global');
  const otherEntries   = filtered.filter(([, e]) => e.kind === 'other');

  // Step groups — ordered by appearance in the live step list for stable display.
  const stepIds = [...new Set(
    filtered
      .filter(([, e]) => e.stepId && e.kind !== 'trigger' && e.kind !== 'global' && e.kind !== 'other')
      .map(([, e]) => e.stepId!),
  )].sort((a, b) => {
    const ai = steps.findIndex((s) => s.stepId === a);
    const bi = steps.findIndex((s) => s.stepId === b);
    return (ai === -1 ? 9999 : ai) - (bi === -1 ? 9999 : bi);
  });

  // Filter typing auto-expands groups that contain matches — collapsed groups can't
  // be searched visually otherwise.
  const effectiveExpandedGroups = filter.length > 0
    ? new Set([
        ...(triggerEntries.length > 0 ? ['__trigger'] : []),
        ...(globalEntries.length > 0 ? ['__global'] : []),
        ...stepIds,
        ...(otherEntries.length > 0 ? ['__other'] : []),
      ])
    : expandedGroups;

  const allGroupKeys: string[] = [
    ...(triggerEntries.length > 0 ? ['__trigger'] : []),
    ...(globalEntries.length > 0 ? ['__global'] : []),
    ...stepIds,
    ...(otherEntries.length > 0 ? ['__other'] : []),
  ];
  const expandAllGroups = () => setExpandedGroups(new Set(allGroupKeys));
  const collapseAllGroups = () => setExpandedGroups(new Set());

  return (
    <div className="flex flex-col h-full">
      <div className="px-3 py-2 border-b border-outline-variant/10 shrink-0 flex items-center gap-2">
        <input
          type="text"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder={t('execution.output.filterPlaceholder')}
          className="flex-1 px-2.5 py-1.5 text-xs font-mono bg-surface-high border border-outline-variant/20 rounded-md outline-none focus:border-primary/50 placeholder:text-outline"
        />
        <button
          type="button"
          onClick={expandAllGroups}
          title={t('execution.output.expandAllGroups')}
          className="text-[10px] font-label text-primary hover:underline px-1 shrink-0"
        >
          {t('execution.output.expandAll')}
        </button>
        <button
          type="button"
          onClick={collapseAllGroups}
          title={t('execution.output.collapseAllGroups')}
          className="text-[10px] font-label text-on-surface-variant hover:underline px-1 shrink-0"
        >
          {t('execution.output.collapse')}
        </button>
      </div>
      {!allEntries.some(([, e]) => e.kind === 'trigger' || e.kind === 'global') && (
        <div className="px-3 py-1.5 bg-primary/10 border-b border-primary/20 shrink-0">
          <span className="font-label text-[10px] text-primary">
            {t('execution.output.breakpointTip')}
          </span>
        </div>
      )}
      <div className="flex-1 overflow-y-auto divide-y divide-outline-variant/10">
        {triggerEntries.length > 0 && (
          <EntryGroup
            groupKey="__trigger" label={t('execution.output.groupTrigger')} entries={triggerEntries}
            isExpanded={effectiveExpandedGroups.has('__trigger')} onToggleGroup={toggleGroup}
            expandedKeys={expandedKeys} copied={copied} onToggle={toggle} onCopy={copy}
          />
        )}
        {globalEntries.length > 0 && (
          <EntryGroup
            groupKey="__global" label={t('execution.output.groupGlobal')} entries={globalEntries}
            isExpanded={effectiveExpandedGroups.has('__global')} onToggleGroup={toggleGroup}
            expandedKeys={expandedKeys} copied={copied} onToggle={toggle} onCopy={copy}
          />
        )}
        {stepIds.map((stepId) => {
          const stepEntries = filtered.filter(([, e]) => e.stepId === stepId);
          const step = steps.find((s) => s.stepId === stepId);
          return (
            <EntryGroup
              key={stepId}
              groupKey={stepId}
              label={step?.stepName || stepId}
              status={step?.status}
              entries={stepEntries}
              isExpanded={effectiveExpandedGroups.has(stepId)}
              onToggleGroup={toggleGroup}
              expandedKeys={expandedKeys}
              copied={copied}
              onToggle={toggle}
              onCopy={copy}
            />
          );
        })}
        {otherEntries.length > 0 && (
          <EntryGroup
            groupKey="__other" label={t('execution.output.groupOther')} entries={otherEntries}
            isExpanded={effectiveExpandedGroups.has('__other')} onToggleGroup={toggleGroup}
            expandedKeys={expandedKeys} copied={copied} onToggle={toggle} onCopy={copy}
          />
        )}
        {filtered.length === 0 && filter.length > 0 && (
          <div className="flex items-center justify-center py-8 text-outline font-label text-xs">
            {t('execution.output.noEntriesMatch', { filter })}
          </div>
        )}
      </div>
    </div>
  );
}

function EntryGroup({ groupKey, label, status, entries, isExpanded, onToggleGroup, expandedKeys, copied, onToggle, onCopy }: Readonly<{
  groupKey: string;
  label: string;
  status?: string;
  entries: [string, DatabusEntry][];
  isExpanded: boolean;
  onToggleGroup: (groupKey: string) => void;
  expandedKeys: Set<string>;
  copied: string | null;
  onToggle: (key: string) => void;
  onCopy: (key: string, value: string) => void;
}>) {
  const { t } = useTranslation('designer');
  // Sort: output first, error second, params last (alphabetically).
  const sorted = [...entries].sort(([, a], [, b]) => {
    const order = { output: 0, error: 1, param: 2, trigger: 3, global: 4, other: 5 };
    const diff = (order[a.kind] ?? 5) - (order[b.kind] ?? 5);
    if (diff !== 0) return diff;
    return (a.paramKey ?? '').localeCompare(b.paramKey ?? '');
  });

  return (
    <div>
      <button
        type="button"
        onClick={() => onToggleGroup(groupKey)}
        className="w-full flex items-center gap-2 px-3 py-1 bg-surface-low/60 sticky top-0 z-10 hover:bg-surface-low transition-colors text-left"
        title={isExpanded ? t('execution.output.clickToCollapse') : t('execution.output.clickToExpand')}
      >
        <ChevronRight
          size={11}
          className={`text-on-surface-variant transition-transform ${isExpanded ? 'rotate-90' : ''}`}
        />
        {status && <StepStatusIcon status={status} size={11} />}
        <span className="font-label text-[10px] font-bold uppercase tracking-wide text-on-surface-variant flex-1">
          {label}
        </span>
        <span className="font-mono text-[10px] text-outline">{entries.length}</span>
      </button>
      {isExpanded && sorted.map(([key, entry]) => (
        <EntryRow
          key={key}
          entryKey={key}
          entry={entry}
          isExpanded={expandedKeys.has(key)}
          isCopied={copied === key}
          onToggle={onToggle}
          onCopy={onCopy}
        />
      ))}
    </div>
  );
}

const KIND_BADGE: Record<DatabusEntry['kind'], { label: string; cls: string }> = {
  output:  { label: 'OUT', cls: 'bg-green-100 dark:bg-green-900/40 text-green-700 dark:text-green-400' },
  error:   { label: 'ERR', cls: 'bg-red-100 dark:bg-red-900/40 text-red-600 dark:text-red-400' },
  param:   { label: 'PAR', cls: 'bg-indigo-100 dark:bg-indigo-900/40 text-indigo-700 dark:text-indigo-400' },
  trigger: { label: 'TRG', cls: 'bg-blue-100 dark:bg-blue-900/40 text-blue-700 dark:text-blue-400' },
  global:  { label: 'GLB', cls: 'bg-amber-100 dark:bg-amber-900/40 text-amber-700 dark:text-amber-400' },
  other:   { label: '···', cls: 'bg-surface-high text-on-surface-variant' },
};

function EntryRow({ entryKey, entry, isExpanded, isCopied, onToggle, onCopy }: Readonly<{
  entryKey: string;
  entry: DatabusEntry;
  isExpanded: boolean;
  isCopied: boolean;
  onToggle: (key: string) => void;
  onCopy: (key: string, value: string) => void;
}>) {
  const { t } = useTranslation('designer');
  const badge = KIND_BADGE[entry.kind];
  const expandable = entry.value.includes('\n') || entry.value.length > 100;
  // Within a step group, strip the step-ID prefix so the key stays compact.
  const displayKey = entry.stepId ? entryKey.slice(entry.stepId.length + 1) : entryKey;

  return (
    <div className="px-3 py-1.5 hover:bg-surface-low/30 group flex items-start gap-2 min-w-0">
      <span className={`shrink-0 inline-flex items-center px-1 py-px rounded text-[9px] font-bold font-mono mt-0.5 ${badge.cls}`}>
        {badge.label}
      </span>
      <button
        onClick={() => expandable && onToggle(entryKey)}
        className={`flex-1 min-w-0 text-left ${expandable ? 'cursor-pointer' : 'cursor-default'}`}
      >
        <div className="flex items-baseline gap-2 min-w-0">
          <span className="font-mono text-[10px] text-on-surface-variant shrink-0">{displayKey}</span>
          {!isExpanded && (
            <span className="font-mono text-[11px] text-on-surface truncate flex-1">{entry.value}</span>
          )}
        </div>
        {isExpanded && (
          <pre className="mt-0.5 text-[11px] font-mono text-on-surface whitespace-pre-wrap break-all leading-relaxed">
            {entry.value}
          </pre>
        )}
      </button>
      <button
        onClick={() => onCopy(entryKey, entry.value)}
        className={`shrink-0 p-0.5 rounded transition-all ${
          isCopied
            ? 'opacity-100 text-green-600'
            : 'opacity-0 group-hover:opacity-100 text-outline hover:text-on-surface hover:bg-surface-highest'
        }`}
        title={t('execution.output.copyValue')}
      >
        {isCopied ? <CheckmarkFilled size={12} /> : <Copy size={12} />}
      </button>
    </div>
  );
}
