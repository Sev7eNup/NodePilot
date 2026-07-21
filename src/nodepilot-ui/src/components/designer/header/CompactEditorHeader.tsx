import { Redo, Search, Undo, WarningAltFilled } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useDesignStore } from '../../../stores/designStore';
import { ActivityTypeFilter } from '../overlays/ActivityTypeFilter';
import { ToolbarGlow } from './ToolbarGlow';
import { ToolbarSection } from './ToolbarSection';
import { ViewMenu } from './ViewMenu';
import { OverlaysMenu } from './OverlaysMenu';
import { ToolsMenu } from './ToolsMenu';
import { WorkflowNameField } from './WorkflowNameField';
import { EditorIdentity } from './EditorIdentity';
import { SkinSwitcher } from './SkinSwitcher';
import { ToolbarLayoutToggle } from './ToolbarLayoutToggle';
import { AtelierThemeToggle } from './AtelierThemeToggle';
import { RunControls } from './RunControls';
import { LifecycleControls } from './LifecycleControls';
import { StandardMoreMenu } from './StandardMoreMenu';
import type { EditorHeaderProps } from './editorHeaderTypes';

/**
 * Compact editor header — the default single-row, three-zone layout:
 *   LEFT   identity (back, brand, Standard/Expert, AI assistant)
 *   CENTER the workflow name (centered, click-to-rename)
 *   RIGHT  a compact toolbar: history, search, the View/Overlays/Tools menus that hold the many
 *          canvas/inspect controls, the green Run button, the lint pill, lifecycle actions, the
 *          layout toggle and the skin switcher.
 * Selected via `designStore.toolbarLayout === 'compact'` by the {@link EditorHeader} dispatcher.
 */
export function CompactEditorHeader({
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
  const { t } = useTranslation('editor');
  const isExpert = useDesignStore((s) => s.designerMode === 'expert');

  return (
    <header className="np-editor-header relative z-[45] flex items-center gap-4 bg-surface-lowest px-6 py-3 shrink-0 border-b border-outline-variant/15">
      {/* ── LEFT: identity ─────────────────────────────────────────────────── */}
      <EditorIdentity aiChatOpen={aiChatOpen} onToggleAiChat={onToggleAiChat} />
      {/* ── CENTER: workflow name — centered, click-to-rename ───────────────── */}
      <div className="flex-1 min-w-0 flex items-center justify-center px-2">
        <WorkflowNameField
          name={name}
          onRename={onRename}
          canWrite={canWrite}
          placeholder={t('workflowNamePlaceholder')}
        />
      </div>
      {/* ── RIGHT: tools + actions ─────────────────────────────────────────── */}
      <div className="shrink-0 flex items-center">
        <ToolbarGlow className="flex items-center gap-2">
          {/* History. Always visible. */}
          <ToolbarSection id="history">
            <button
              onClick={undo}
              disabled={historyPast.length === 0}
              className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors disabled:opacity-30 disabled:cursor-not-allowed"
              title={t('undoTooltip')}
            >
              <Undo size={16} />
            </button>
            <button
              onClick={redo}
              disabled={historyFuture.length === 0}
              className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors disabled:opacity-30 disabled:cursor-not-allowed"
              title={t('redoTooltip')}
            >
              <Redo size={16} />
            </button>
          </ToolbarSection>

          {/* Inspect: search + the View/Overlays/Tools menus. Standard mode gets StandardMoreMenu. */}
          <ToolbarSection id="inspect">
            <button
              onClick={() => setSearchOpen(true)}
              className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors"
              title={t('searchTooltip')}
            >
              <Search size={16} />
            </button>
            {isExpert && <ViewMenu />}
            {isExpert && <OverlaysMenu />}
            {isExpert && (
              <ActivityTypeFilter nodes={nodes} hiddenTypes={hiddenActivityTypes} onChange={setHiddenActivityTypes} />
            )}
            {isExpert && (
              <ToolsMenu
                workflowId={workflowId}
                workflowName={name}
                nodes={nodes}
                setFindReplaceOpen={setFindReplaceOpen}
                zoomToSelection={zoomToSelection}
                setDiffOpen={setDiffOpen}
                simulation={simulation}
                runSimulation={runSimulation}
                clearSimulation={clearSimulation}
                setHelpOpen={setHelpOpen}
                tidyLayout={tidyLayout}
                isTidying={isTidying}
                layoutMode={layoutMode}
                restoreOrigLayout={restoreOrigLayout}
                hasOrigLayout={hasOrigLayout}
                exportPng={exportPng}
              />
            )}
            {!isExpert && hiddenActivityTypes.size > 0 && (
              <button
                type="button"
                onClick={() => setHiddenActivityTypes(new Set())}
                className="flex items-center gap-1 rounded-md h-9 px-2.5 bg-warning-container text-on-warning-container text-xs font-label font-semibold"
                title={t('designerMode.clearHiddenTypes')}
              >
                <WarningAltFilled size={15} /> {hiddenActivityTypes.size}
              </button>
            )}
            {!isExpert && (
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

          {/* Run + lint (green CTA). */}
          <ToolbarSection id="run">
            <RunControls
              variant="cta"
              roleCanWrite={roleCanWrite}
              liveExecution={liveExecution}
              handleRunClick={handleRunClick}
              lintResult={lintResult}
              setLintPanelOpen={setLintPanelOpen}
            />
          </ToolbarSection>

          {/* Lifecycle. Edit-lock toggle + Save + Publish/Disable. Admin/Operator only. */}
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

          {/* Atelier-design switch + toolbar-layout toggle + colour-skin switcher — trailing, always visible. */}
          <AtelierThemeToggle />
          <ToolbarLayoutToggle />
          <SkinSwitcher />
        </ToolbarGlow>
      </div>
    </header>
  );
}
