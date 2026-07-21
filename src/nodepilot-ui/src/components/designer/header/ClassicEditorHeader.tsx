import {
  ArrowsHorizontal,
  Chemistry,
  Compare,
  Download,
  ImageCopy,
  Keyboard,
  MagicWand,
  Redo,
  Reset,
  Search,
  SearchLocate,
  Undo,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { downloadFromApi } from '../../../api/client';
import { useDesignStore } from '../../../stores/designStore';
import { toast } from '../../../stores/toastStore';
import { ActivityTypeFilter } from '../overlays/ActivityTypeFilter';
import { ToolbarGlow } from './ToolbarGlow';
import { ToolbarSection } from './ToolbarSection';
import { EditorIdentity } from './EditorIdentity';
import { SkinSwitcher } from './SkinSwitcher';
import { ToolbarLayoutToggle } from './ToolbarLayoutToggle';
import { AtelierThemeToggle } from './AtelierThemeToggle';
import { RunControls } from './RunControls';
import { LifecycleControls } from './LifecycleControls';
import { StandardMoreMenu } from './StandardMoreMenu';
import { ClassicDesignToggles } from './ClassicDesignToggles';
import { ClassicOverlayToggles } from './ClassicOverlayToggles';
import type { EditorHeaderProps } from './editorHeaderTypes';

// Plain action buttons are transparent and let the tinted ToolbarSection tray show through as
// their resting fill (identical to the compact toolbar); they fill on hover. Active toggles keep
// their own tint. This is the treatment index.css expects — opaque/tinted controls at z-1 over
// the section, the proximity bloom behind at z-0.
const iconBtn = 'flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors disabled:opacity-30 disabled:cursor-not-allowed';

/**
 * Classic editor header — the pre-redesign layout where every control is its own inline button
 * in a row (no grouped popover menus, no green CTA, right-aligned name). Selected via
 * `designStore.toolbarLayout === 'classic'` by the {@link EditorHeader} dispatcher.
 *
 * Each cluster is a {@link ToolbarSection} inside {@link ToolbarGlow}, so the cursor-proximity
 * glow + the subtle tinted tray background work exactly like the compact toolbar. Responsive rule: the toolbar wraps
 * whole clusters onto multiple lines (`flex-wrap`) instead of overflowing — the header grows taller
 * and the canvas below shrinks. The layout toggle + skin switcher are last so they stay reachable
 * even when wrapped.
 */
export function ClassicEditorHeader({
  workflowId, workflow, name, onRename, isDirty, canWrite, nodes,
  aiChatOpen, onToggleAiChat,
  undo, redo, historyPast, historyFuture,
  tidyLayout, isTidying, layoutMode, restoreOrigLayout, hasOrigLayout,
  setSearchOpen, setFindReplaceOpen, zoomToSelection, setDiffOpen,
  simulation, runSimulation, clearSimulation, lintResult, setLintPanelOpen, setHelpOpen,
  hiddenActivityTypes, setHiddenActivityTypes,
  liveExecution, handleRunClick,
  exportPng, onSave, isPublishing, onRequestPublish,
  roleCanWrite, isLockedByMe, isLockedByOther,
  onLock, isLocking, onUnlock, isUnlocking, onDisable, isDisabling, isEnabling,
}: Readonly<EditorHeaderProps>) {
  const { t } = useTranslation(['editor', 'common']);
  const isExpert = useDesignStore((s) => s.designerMode === 'expert');

  return (
    <header className="np-editor-header relative z-[45] flex flex-wrap items-center gap-x-4 gap-y-2 bg-surface-lowest px-6 py-3 shrink-0 border-b border-outline-variant/15">
      <EditorIdentity aiChatOpen={aiChatOpen} onToggleAiChat={onToggleAiChat} />
      {/* Right block: right-aligned auto-grow name, then the inline clusters. `flex-wrap` breaks
          whole clusters onto the next line rather than overflowing on narrow viewports. */}
      <div className="flex-1 min-w-0 flex flex-wrap items-center justify-end gap-x-4 gap-y-2">
        {/* Auto-grow workflow name — a hidden mirror span forces the grid cell width. */}
        <div className="inline-grid items-center shrink-0 max-w-full">
          <input
            type="text"
            value={name}
            onChange={(event) => onRename(event.target.value)}
            disabled={!canWrite}
            placeholder={t('editor:workflowNamePlaceholder')}
            aria-label={t('editor:workflowNamePlaceholder')}
            className="[grid-area:1/1] min-w-[1ch] p-0 text-sm font-label font-medium text-on-surface-variant text-right bg-transparent border-none outline-none disabled:opacity-100 disabled:cursor-default focus:text-on-surface"
            title={name}
          />
          <span aria-hidden className="[grid-area:1/1] invisible whitespace-pre p-0 text-sm font-label font-medium">
            {name || t('editor:workflowNamePlaceholder')}
          </span>
        </div>

        <ToolbarGlow className="flex flex-wrap items-center justify-end gap-x-4 gap-y-2">
          {/* History */}
          <ToolbarSection id="history">
            <button onClick={undo} disabled={historyPast.length === 0} className={iconBtn} title={t('editor:undoTooltip')}>
              <Undo size={16} />
            </button>
            <button onClick={redo} disabled={historyFuture.length === 0} className={iconBtn} title={t('editor:redoTooltip')}>
              <Redo size={16} />
            </button>
          </ToolbarSection>

          {/* Layout */}
          {canWrite && (
            <ToolbarSection id="layout">
              <button onClick={tidyLayout} disabled={isTidying} className={iconBtn} title={t('editor:tidyTooltip', { mode: layoutMode })}>
                <MagicWand size={16} />
              </button>
              {isExpert && (
                <button onClick={restoreOrigLayout} disabled={!hasOrigLayout} className={iconBtn} title={t('editor:restoreOrigLayout')}>
                  <Reset size={16} />
                </button>
              )}
            </ToolbarSection>
          )}

          {/* Inspect */}
          <ToolbarSection id="inspect">
            <button onClick={() => setSearchOpen(true)} className={iconBtn} title={t('editor:searchTooltip')}>
              <Search size={16} />
            </button>
            {isExpert && (
              <>
                <button onClick={() => setFindReplaceOpen(true)} className={iconBtn} title={t('editor:findReplaceTooltip')}>
                  <ArrowsHorizontal size={16} />
                </button>
                <button onClick={zoomToSelection} className={iconBtn} title={t('editor:zoomToSelectionTooltip')}>
                  <SearchLocate size={16} />
                </button>
                <button onClick={() => setDiffOpen(true)} disabled={!workflowId} className={iconBtn} title={t('editor:diffTooltip')}>
                  <Compare size={16} />
                </button>
                <button
                  onClick={() => (simulation ? clearSimulation() : runSimulation())}
                  className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
                    simulation ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
                  }`}
                  title={simulation ? t('editor:simulationClear') : t('editor:simulationRun')}
                >
                  <Chemistry size={16} />
                </button>
                <button onClick={() => setHelpOpen(true)} className={iconBtn} title={t('editor:shortcutsTooltip')}>
                  <Keyboard size={16} />
                </button>
              </>
            )}
            {!isExpert && hiddenActivityTypes.size > 0 && (
              <button
                type="button"
                onClick={() => setHiddenActivityTypes(new Set())}
                className="flex items-center gap-1 rounded-md h-9 px-2.5 bg-warning-container text-on-warning-container text-xs font-label font-semibold"
                title={t('editor:designerMode.clearHiddenTypes')}
              >
                {hiddenActivityTypes.size}
              </button>
            )}
          </ToolbarSection>

          {/* View: inline design toggles + activity-type filter */}
          {isExpert && (
            <ToolbarSection id="view">
              <ClassicDesignToggles />
              <ActivityTypeFilter nodes={nodes} hiddenTypes={hiddenActivityTypes} onChange={setHiddenActivityTypes} />
            </ToolbarSection>
          )}

          {/* Run + inline overlay toggles */}
          <ToolbarSection id="run">
            <RunControls
              variant="icon"
              roleCanWrite={roleCanWrite}
              liveExecution={liveExecution}
              handleRunClick={handleRunClick}
              lintResult={lintResult}
              setLintPanelOpen={setLintPanelOpen}
            />
            {isExpert && <ClassicOverlayToggles />}
          </ToolbarSection>

          {/* Lifecycle */}
          {roleCanWrite && (
            <ToolbarSection id="lifecycle">
              <LifecycleControls
                workflow={workflow}
                canWrite={canWrite}
                isDirty={isDirty}
                isLockedByMe={isLockedByMe}
                isLockedByOther={isLockedByOther}
                onLock={onLock}
                isLocking={isLocking}
                onUnlock={onUnlock}
                isUnlocking={isUnlocking}
                onSave={onSave}
                isPublishing={isPublishing}
                onRequestPublish={onRequestPublish}
                onDisable={onDisable}
                isDisabling={isDisabling}
                isEnabling={isEnabling}
              />
            </ToolbarSection>
          )}

          {/* Export */}
          <ToolbarSection id="export">
            {isExpert ? (
              <>
                <button
                  onClick={async () => {
                    if (!workflowId) return;
                    try {
                      await downloadFromApi(`/workflows/${workflowId}/export`, `${name || 'workflow'}.workflow.json`);
                    } catch (err) {
                      toast.error(t('common:exportFailed', { message: (err as Error).message }));
                    }
                  }}
                  disabled={!workflowId}
                  className={iconBtn}
                  title={t('editor:exportJsonTitle')}
                >
                  <Download size={16} />
                </button>
                <button onClick={exportPng} disabled={nodes.length === 0} className={iconBtn} title={t('editor:exportPngTitle')}>
                  <ImageCopy size={16} />
                </button>
              </>
            ) : (
              <StandardMoreMenu
                workflowId={workflowId}
                workflowName={name}
                hasNodes={nodes.length > 0}
                exportPng={exportPng}
                openDiff={() => setDiffOpen(true)}
                openHelp={() => setHelpOpen(true)}
              />
            )}
          </ToolbarSection>
        </ToolbarGlow>

        {/* Atelier switch + layout toggle + skin switcher — last group, always reachable even when wrapped. */}
        <div className="flex items-center gap-1">
          <AtelierThemeToggle />
          <ToolbarLayoutToggle />
          <SkinSwitcher />
        </div>
      </div>
    </header>
  );
}
