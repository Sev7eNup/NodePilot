import { OverflowMenuHorizontal } from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { downloadFromApi } from '../../../api/client';
import { toast } from '../../../stores/toastStore';
import { MenuButton } from './menuPrimitives';

/**
 * Standard-mode overflow menu — folds the less-frequent actions (diff, shortcuts, export)
 * behind a single "…" button so the standard toolbar stays minimal. Shared by both editor-header
 * layouts (extracted so the classic layout can reuse it, not just the compact one).
 */
export function StandardMoreMenu({
  workflowId, workflowName, hasNodes, exportPng, openDiff, openHelp,
}: Readonly<{
  workflowId: string | undefined;
  workflowName: string;
  hasNodes: boolean;
  exportPng: () => void;
  openDiff: () => void;
  openHelp: () => void;
}>) {
  const { t } = useTranslation(['editor', 'common']);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as globalThis.Node)) setOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [open]);

  const run = (action: () => void) => () => {
    setOpen(false);
    action();
  };

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        aria-haspopup="menu"
        title={t('editor:designerMode.more')}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          open ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <OverflowMenuHorizontal size={16} />
      </button>
      {open && (
        <div className="absolute right-0 top-full z-50 mt-1 min-w-48 overflow-hidden rounded-md border border-outline-variant/30 bg-surface-lowest py-1 shadow-xl" role="menu">
          <MenuButton onClick={run(openDiff)} disabled={!workflowId} title={t('editor:diffTooltip')}>{t('editor:diffTooltip')}</MenuButton>
          <MenuButton onClick={run(openHelp)} title={t('editor:shortcutsTooltip')}>{t('editor:shortcutsTooltip')}</MenuButton>
          <MenuButton
            disabled={!workflowId}
            title={t('editor:exportJsonTitle')}
            onClick={run(async () => {
              if (!workflowId) return;
              try {
                await downloadFromApi(`/workflows/${workflowId}/export`, `${workflowName || 'workflow'}.workflow.json`);
              } catch (err) {
                toast.error(t('common:exportFailed', { message: (err as Error).message }));
              }
            })}
          >
            {t('editor:exportJsonTitle')}
          </MenuButton>
          <MenuButton onClick={run(exportPng)} disabled={!hasNodes} title={t('editor:exportPngTitle')}>{t('editor:exportPngTitle')}</MenuButton>
        </div>
      )}
    </div>
  );
}
