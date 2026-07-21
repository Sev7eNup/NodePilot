import { ArrowsHorizontal, Close, Search } from '@carbon/icons-react';
import { useRef, useEffect, useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { type Node, type Edge } from '@xyflow/react';
import { findMatches, applyReplaceAll, type MatchLocation, type FindReplaceScopes } from '../../../lib/findReplace';

export function FindReplaceOverlay({ nodes, edges, onApply, onClose }: Readonly<{
  nodes: Node[];
  edges: Edge[];
  onApply: (nodes: Node[], edges: Edge[]) => void;
  onClose: () => void;
}>) {
  const { t } = useTranslation('editor');
  const searchRef = useRef<HTMLInputElement>(null);
  const [searchValue, setSearchValue] = useState('');
  const [replaceValue, setReplaceValue] = useState('');
  const [selectedIdx, setSelectedIdx] = useState(0);
  const [scopes, setScopes] = useState<FindReplaceScopes>({
    nodeLabels: true,
    edgeLabels: true,
    configValues: true,
  });

  useEffect(() => {
    requestAnimationFrame(() => searchRef.current?.select());
  }, []);

  const matches = useMemo(
    () => findMatches(searchValue, nodes, edges, scopes),
    [searchValue, nodes, edges, scopes],
  );

  useEffect(() => setSelectedIdx(0), [matches.length]);

  function handleReplaceOne() {
    if (!matches.length) return;
    const match = matches[selectedIdx] ?? matches[0];
    const { nodes: newNodes, edges: newEdges } = applyReplaceAll([match], searchValue, replaceValue, nodes, edges);
    onApply(newNodes, newEdges);
  }

  function handleReplaceAll() {
    if (!matches.length) return;
    const { nodes: newNodes, edges: newEdges } = applyReplaceAll(matches, searchValue, replaceValue, nodes, edges);
    onApply(newNodes, newEdges);
    onClose();
  }

  function toggleScope(key: keyof FindReplaceScopes) {
    setScopes((s) => ({ ...s, [key]: !s[key] }));
  }

  const kindLabel: Record<MatchLocation['kind'], string> = {
    'node-label': t('findReplaceOverlay.kindLabel'),
    'edge-label': t('findReplaceOverlay.kindEdge'),
    'config': t('findReplaceOverlay.kindConfig'),
  };

  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-start justify-center pt-24 bg-black/20"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="w-[540px] max-w-[90vw] bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30 overflow-hidden"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        {/* Header */}
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant/20 bg-surface-low">
          <Search size={14} className="text-on-surface-variant shrink-0" />
          <span className="font-label text-xs font-bold uppercase tracking-wide text-on-surface-variant">{t('findReplaceOverlay.title')}</span>
          <button onClick={onClose} className="ml-auto text-on-surface-variant hover:text-on-surface" aria-label={t('common:close')}>
            <Close size={14} />
          </button>
        </div>

        {/* Find field */}
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant/10">
          <Search size={14} className="text-outline shrink-0" />
          <input
            ref={searchRef}
            type="text"
            value={searchValue}
            onChange={(e) => setSearchValue(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
            placeholder={t('findReplaceOverlay.findPlaceholder')}
            className="flex-1 bg-transparent outline-none text-sm font-label text-on-surface placeholder:text-outline"
          />
          {matches.length > 0 && (
            <span className="text-[10px] font-label text-outline shrink-0">{t('findReplaceOverlay.matchesCount', { count: matches.length })}</span>
          )}
        </div>

        {/* Replace field */}
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant/10">
          <ArrowsHorizontal size={14} className="text-outline shrink-0" />
          <input
            type="text"
            value={replaceValue}
            onChange={(e) => setReplaceValue(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Escape') { onClose(); return; }
              if (e.key === 'Enter') handleReplaceAll();
            }}
            placeholder={t('findReplaceOverlay.replacePlaceholder')}
            className="flex-1 bg-transparent outline-none text-sm font-label text-on-surface placeholder:text-outline"
          />
          <button
            onClick={handleReplaceOne}
            disabled={!matches.length}
            className="px-2.5 py-1 text-[10px] font-label font-semibold rounded bg-surface-high hover:bg-surface-highest text-on-surface-variant disabled:opacity-40 transition-colors shrink-0"
            title={t('findReplaceOverlay.replaceOneTitle')}
          >{t('findReplaceOverlay.replace')}</button>
          <button
            onClick={handleReplaceAll}
            disabled={!matches.length}
            className="px-2.5 py-1 text-[10px] font-label font-semibold rounded bg-primary/15 hover:bg-primary/25 text-primary disabled:opacity-40 transition-colors shrink-0"
            title={t('findReplaceOverlay.replaceAllTitle', { count: matches.length })}
          >{t('findReplaceOverlay.replaceAll', { count: matches.length })}</button>
        </div>

        {/* Scope toggles */}
        <div className="flex items-center gap-3 px-4 py-2 border-b border-outline-variant/10 bg-surface-low/50">
          <span className="font-label text-[10px] font-bold uppercase tracking-wide text-outline">{t('findReplaceOverlay.searchIn')}</span>
          {(['nodeLabels', 'edgeLabels', 'configValues'] as const).map((key) => (
            <label key={key} className="flex items-center gap-1.5 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={scopes[key]}
                onChange={() => toggleScope(key)}
                className="accent-primary w-3 h-3"
              />
              <span className="font-label text-[11px] text-on-surface-variant">
                {key === 'nodeLabels' ? t('findReplaceOverlay.nodeLabels') : key === 'edgeLabels' ? t('findReplaceOverlay.edgeLabels') : t('findReplaceOverlay.configValues')}
              </span>
            </label>
          ))}
        </div>

        {/* Results */}
        <div className="max-h-64 overflow-y-auto">
          {searchValue.trim() === '' ? (
            <div className="px-4 py-5 text-center text-xs font-label text-on-surface-variant">
              {t('findReplaceOverlay.emptyHint')}
            </div>
          ) : matches.length === 0 ? (
            <div className="px-4 py-5 text-center text-xs font-label text-on-surface-variant">{t('findReplaceOverlay.noMatches')}</div>
          ) : (
            matches.map((m, i) => (
              <button
                key={`${m.nodeId ?? m.edgeId}-${m.kind}-${m.configKeyPath ?? ''}-${i}`}
                type="button"
                onClick={() => setSelectedIdx(i)}
                className={`w-full flex items-start gap-2 px-4 py-2 text-left transition-colors border-b border-outline-variant/10 last:border-0 ${selectedIdx === i ? 'bg-primary/8 text-primary' : 'hover:bg-surface-high'}`}
              >
                <span className={`shrink-0 mt-0.5 px-1.5 py-0.5 rounded text-[9px] font-label font-bold uppercase ${
                  m.kind === 'node-label' ? 'bg-indigo-100 text-indigo-700' :
                  m.kind === 'edge-label' ? 'bg-amber-100 text-amber-700' :
                  'bg-teal-100 text-teal-700'
                }`}>{kindLabel[m.kind]}</span>
                <div className="min-w-0 flex-1">
                  <div className="font-label text-xs font-semibold text-on-surface truncate">{m.displayName}</div>
                  <div className="font-mono text-[10px] text-on-surface-variant truncate mt-0.5">{m.contextSnippet}</div>
                </div>
              </button>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
