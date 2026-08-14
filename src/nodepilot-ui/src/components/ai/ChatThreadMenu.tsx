import { Add, ChevronDown, Edit, TrashCan } from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatThreadMeta } from '../../stores/aiChatStore';

/**
 * Compact thread switcher: active chat name + dropdown (switch/rename/delete/new). Shared by the
 * docked workflow assistant and the global AI-Chat page, which only differ in how the trigger is
 * chromed and on which side the panel is anchored — both are passed in verbatim so each surface
 * keeps its exact look. `align` is mapped to a literal utility class (never interpolated) so
 * Tailwind's source scan still sees both variants.
 */
export function ChatThreadMenu({
  threads, activeId, disabled, onSelect, onNew, onRename, onDelete, triggerClassName, align,
}: Readonly<{
  threads: ChatThreadMeta[];
  activeId: string;
  disabled?: boolean;
  onSelect: (id: string) => void;
  onNew: () => void;
  onRename: (id: string, name: string) => void;
  onDelete: (id: string) => void;
  /** Full class string for the trigger button — the two call sites are chromed differently. */
  triggerClassName: string;
  /** Which edge of the trigger the dropdown is anchored to. */
  align: 'left' | 'right';
}>) {
  const { t } = useTranslation(['ai']);
  const [open, setOpen] = useState(false);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) { setOpen(false); setRenaming(null); }
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  const active = threads.find((th) => th.id === activeId);
  const activeName = active?.name ?? t('ai:chat.threadDefault', { n: 1 });

  const commitRename = (id: string) => {
    const name = renameValue.trim();
    if (name) onRename(id, name);
    setRenaming(null);
  };

  const alignClass = align === 'right' ? 'right-0' : 'left-0';

  return (
    <div ref={ref} className="relative min-w-0">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className={triggerClassName}
        title={t('ai:chat.threads')}
        aria-label={t('ai:chat.threads')}
      >
        <span className="max-w-[11rem] truncate">{activeName}</span>
        <ChevronDown size={13} className="shrink-0 text-on-surface-variant" />
      </button>
      {open && (
        <div className={`absolute ${alignClass} top-full z-20 mt-1 w-64 rounded-lg border border-outline-variant/30 bg-surface-low p-1 shadow-lg`}>
          <div className="max-h-64 overflow-y-auto">
            {threads.length === 0 && (
              <p className="px-2 py-1.5 text-xs text-on-surface-variant">{t('ai:chat.noThreads')}</p>
            )}
            {threads.map((th) => (
              <div
                key={th.id}
                className={`group/th flex items-center gap-1 rounded px-1.5 py-1 ${th.id === activeId ? 'bg-surface-high' : 'hover:bg-surface-high'}`}
              >
                {renaming === th.id ? (
                  <input
                    autoFocus
                    value={renameValue}
                    onChange={(e) => setRenameValue(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') commitRename(th.id);
                      if (e.key === 'Escape') setRenaming(null);
                    }}
                    onBlur={() => commitRename(th.id)}
                    className="min-w-0 flex-1 rounded border border-outline-variant bg-surface-low px-1 py-0.5 text-xs text-on-surface"
                  />
                ) : (
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => { onSelect(th.id); setOpen(false); }}
                    className="min-w-0 flex-1 truncate text-left text-xs text-on-surface disabled:opacity-50"
                  >
                    {th.name}
                  </button>
                )}
                <button
                  type="button"
                  disabled={disabled}
                  title={t('ai:chat.renameThread')}
                  aria-label={t('ai:chat.renameThread')}
                  onClick={() => { setRenaming(th.id); setRenameValue(th.name); }}
                  className="rounded p-0.5 text-on-surface-variant opacity-0 transition-opacity hover:text-on-surface group-hover/th:opacity-100 disabled:hover:text-on-surface-variant"
                >
                  <Edit size={12} />
                </button>
                <button
                  type="button"
                  disabled={disabled}
                  title={t('ai:chat.deleteThread')}
                  aria-label={t('ai:chat.deleteThread')}
                  onClick={() => onDelete(th.id)}
                  className="rounded p-0.5 text-on-surface-variant opacity-0 transition-opacity hover:text-error group-hover/th:opacity-100 disabled:hover:text-on-surface-variant"
                >
                  <TrashCan size={12} />
                </button>
              </div>
            ))}
          </div>
          <button
            type="button"
            disabled={disabled}
            onClick={() => { onNew(); setOpen(false); }}
            className="mt-1 flex w-full items-center gap-1.5 rounded px-1.5 py-1.5 text-xs text-primary hover:bg-surface-high disabled:opacity-40"
          >
            <Add size={13} /> {t('ai:chat.newThread')}
          </button>
        </div>
      )}
    </div>
  );
}
