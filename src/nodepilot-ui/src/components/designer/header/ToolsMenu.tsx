import {
  ArrowsHorizontal,
  Chemistry,
  Compare,
  Download,
  ImageCopy,
  Keyboard,
  MagicWand,
  Reset,
  SearchLocate,
  SettingsAdjust,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';
import { downloadFromApi } from '../../../api/client';
import { toast } from '../../../stores/toastStore';
import { LAYOUT_LABELS, type LayoutMode } from '../../../stores/designStore';
import { usePopover, MenuSectionLabel, MenuButton } from './menuPrimitives';

/**
 * "Werkzeuge" (Tools) menu — groups the expert-only inspect/analysis actions that used to
 * be individual toolbar icons: find & replace, zoom-to-selection, version diff, dry-run
 * simulation, keyboard shortcuts, layout (tidy + restore) and export (JSON/PNG). Stateful
 * rows (tidy/restore/simulation) keep the menu OPEN so they can be clicked repeatedly;
 * overlay-opening rows close it. `title`s mirror the former buttons so tests still resolve.
 */
export function ToolsMenu({
  workflowId, workflowName, nodes,
  setFindReplaceOpen, zoomToSelection, setDiffOpen,
  simulation, runSimulation, clearSimulation, setHelpOpen,
  tidyLayout, isTidying, layoutMode, restoreOrigLayout, hasOrigLayout,
  exportPng,
}: Readonly<{
  workflowId: string | undefined;
  workflowName: string;
  nodes: Node[];
  setFindReplaceOpen: (o: boolean) => void;
  zoomToSelection: () => void;
  setDiffOpen: (o: boolean) => void;
  simulation: unknown;
  runSimulation: () => void;
  clearSimulation: () => void;
  setHelpOpen: (o: boolean) => void;
  tidyLayout: () => void;
  isTidying: boolean;
  layoutMode: LayoutMode;
  restoreOrigLayout: () => void;
  hasOrigLayout: boolean;
  exportPng: () => void;
}>) {
  const { t } = useTranslation(['editor', 'common']);
  const { open, setOpen, ref } = usePopover();

  // Close the menu then run (for overlay-opening actions). Stateful rows call the action
  // directly so the menu stays open for repeated clicks (layout cycling / simulation).
  const closeThen = (fn: () => void) => () => { setOpen(false); fn(); };
  const hasSelection = nodes.some((n) => n.selected);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
        data-testid="tools-menu-trigger"
        title={t('editor:tools.title')}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          open ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <SettingsAdjust size={16} />
      </button>
      {open && (
        <div role="menu" className="np-anim-overlay absolute right-0 top-full mt-1.5 w-64 rounded-xl border border-outline-variant/30 bg-surface-lowest shadow-[var(--np-elev-3)] p-1.5 z-[60] flex flex-col gap-0.5">
          <MenuSectionLabel>{t('editor:tools.inspectHeader')}</MenuSectionLabel>
          <MenuButton icon={<ArrowsHorizontal size={15} />} title={t('editor:findReplaceTooltip')} onClick={closeThen(() => setFindReplaceOpen(true))}>
            {t('editor:findReplaceTooltip')}
          </MenuButton>
          <MenuButton icon={<SearchLocate size={15} />} title={t('editor:zoomToSelectionTooltip')} disabled={!hasSelection} onClick={closeThen(zoomToSelection)}>
            {t('editor:zoomToSelectionTooltip')}
          </MenuButton>
          <MenuButton icon={<Compare size={15} />} title={t('editor:diffTooltip')} disabled={!workflowId} onClick={closeThen(() => setDiffOpen(true))}>
            {t('editor:diffTooltip')}
          </MenuButton>
          <MenuButton icon={<Chemistry size={15} />} title={simulation ? t('editor:simulationClear') : t('editor:simulationRun')} onClick={() => (simulation ? clearSimulation() : runSimulation())}>
            {simulation ? t('editor:simulationClear') : t('editor:simulationRun')}
          </MenuButton>
          <MenuButton icon={<Keyboard size={15} />} title={t('editor:shortcutsTooltip')} onClick={closeThen(() => setHelpOpen(true))}>
            {t('editor:shortcutsTooltip')}
          </MenuButton>

          <MenuSectionLabel>{t('editor:tools.layoutHeader')}</MenuSectionLabel>
          <MenuButton icon={<MagicWand size={15} />} title={t('editor:tidyTooltip', { mode: layoutMode })} disabled={isTidying} onClick={tidyLayout}>
            {t('editor:tidy')} · {LAYOUT_LABELS[layoutMode]}
          </MenuButton>
          <MenuButton icon={<Reset size={15} />} title={t('editor:restoreOrigLayout')} disabled={!hasOrigLayout} onClick={restoreOrigLayout}>
            {t('editor:restoreOrigLayout')}
          </MenuButton>

          <MenuSectionLabel>{t('editor:tools.exportHeader')}</MenuSectionLabel>
          <MenuButton
            icon={<Download size={15} />}
            title={t('editor:exportJsonTitle')}
            disabled={!workflowId}
            onClick={closeThen(async () => {
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
          <MenuButton icon={<ImageCopy size={15} />} title={t('editor:exportPngTitle')} disabled={nodes.length === 0} onClick={closeThen(exportPng)}>
            {t('editor:exportPngTitle')}
          </MenuButton>
        </div>
      )}
    </div>
  );
}
