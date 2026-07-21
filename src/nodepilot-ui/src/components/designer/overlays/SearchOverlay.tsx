import { ArrowUpRight, Close, Search } from '@carbon/icons-react';
import { useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { type Node } from '@xyflow/react';

export function SearchOverlay({ value, onChange, results, onPick, onClose }: Readonly<{
  value: string;
  onChange: (v: string) => void;
  results: Node[];
  onPick: (n: Node) => void;
  onClose: () => void;
}>) {
  const { t } = useTranslation('editor');
  const inputRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    // Autofocus on open, and select the text so that if the user presses Ctrl+F again
    // without closing this overlay first, typing immediately replaces the old search term.
    requestAnimationFrame(() => inputRef.current?.select());
  }, []);
  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-start justify-center pt-24 bg-black/20"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="w-[480px] max-w-[90vw] bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30 overflow-hidden"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center gap-2 px-4 py-3 border-b border-outline-variant/20">
          <Search size={16} className="text-on-surface-variant" />
          <input
            ref={inputRef}
            type="text"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && results.length > 0) { onPick(results[0]); }
              if (e.key === 'Escape') { onClose(); }
            }}
            placeholder={t('searchOverlay.placeholder')}
            className="flex-1 bg-transparent outline-none text-sm font-label text-on-surface placeholder:text-outline"
          />
          <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface" aria-label={t('common:close')}>
            <Close size={14} />
          </button>
        </div>
        <div className="max-h-72 overflow-y-auto">
          {value.trim() === '' ? (
            <div className="px-4 py-6 text-center text-xs font-label text-on-surface-variant">
              {t('searchTypeHint')}
            </div>
          ) : results.length === 0 ? (
            <div className="px-4 py-6 text-center text-xs font-label text-on-surface-variant">
              {t('searchNoResults')}
            </div>
          ) : (
            results.slice(0, 30).map((n) => {
              const d = n.data as Record<string, unknown>;
              const label = (d?.label as string) || n.id;
              const type = (d?.activityType as string) || 'unknown';
              return (
                <button
                  key={n.id}
                  onClick={() => onPick(n)}
                  className="w-full flex items-center gap-3 px-4 py-2 hover:bg-surface-high transition-colors text-left"
                >
                  <ArrowUpRight size={12} className="text-on-surface-variant shrink-0" />
                  <span className="font-label text-sm text-on-surface truncate flex-1">{label}</span>
                  <span className="font-label text-[10px] font-mono text-outline truncate max-w-[120px]">{type}</span>
                </button>
              );
            })
          )}
        </div>
      </div>
    </div>
  );
}
