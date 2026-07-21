import { Apps, ChevronDown, FolderTree, Search, SidePanelClose, SidePanelOpen } from '@carbon/icons-react';
import { useMemo } from 'react';
import type { Node } from '@xyflow/react';
import { useTranslation } from 'react-i18next';
import type { Workflow } from '../../types/api';
import { buildActivityCategories } from './library/activityCategories';
import { ActivityIcon, SnippetsSection, ResizeHandle } from './library/NodeLibrary';
import { WorkflowBrowser } from './WorkflowBrowser';
import { useCustomActivityCatalogStore } from '../../lib/customActivities';
import { useCustomActivityCatalog } from '../../hooks/useCustomActivityCatalog';

type SelectedItem = { type: 'node'; id: string } | { type: 'edge'; id: string } | null;

interface EditorSidebarProps {
  fullscreen: boolean;
  workflowId: string | undefined;
  canWrite: boolean;
  // Tab + collapse state
  leftTab: 'nodes' | 'workflows';
  setLeftTab: (t: 'nodes' | 'workflows') => void;
  leftCollapsed: boolean;
  setLeftCollapsed: (c: boolean) => void;
  // Search + categories
  searchQuery: string;
  setSearchQuery: (q: string) => void;
  collapsedCategories: Record<string, boolean>;
  toggleCategory: (name: string) => void;
  // Resizable panel
  panelSize: number;
  panelHandleProps: { onMouseDown: (e: React.MouseEvent) => void; onDoubleClick: () => void };
  // Node ops
  addNode: (type: string, label: string) => void;
  addSnippet: (snippetId: string) => void;
  // Workflow-Browser tab
  isStartWorkflowSelected: boolean;
  selected: SelectedItem;
  nodes: Node[];
  onOpenWorkflow: (workflow: Workflow) => void;
  onEmbedWorkflow: (workflow: Workflow) => void;
}

/**
 * Tabbed left sidebar of the editor: switches between the Node Library (drag-and-drop
 * activity catalogue + reusable snippets) and the Workflow Browser (open another workflow,
 * or embed it as a sub-workflow into the selected `startWorkflow` step).
 *
 * Owns its own filtered-categories memo and the collapsed/expanded shell. Hidden in
 * fullscreen mode. Renders the resize handle on the right edge when expanded.
 */
export function EditorSidebar({
  fullscreen,
  workflowId,
  canWrite,
  leftTab,
  setLeftTab,
  leftCollapsed,
  setLeftCollapsed,
  searchQuery,
  setSearchQuery,
  collapsedCategories,
  toggleCategory,
  panelSize,
  panelHandleProps,
  addNode,
  addSnippet,
  isStartWorkflowSelected,
  onOpenWorkflow,
  onEmbedWorkflow,
}: Readonly<EditorSidebarProps>) {
  const { t, i18n } = useTranslation(['editor', 'nav']);
  // Hydrate the runtime custom-activity catalog (palette "Custom Nodes" section).
  useCustomActivityCatalog();
  // Subscribe so the palette rebuilds when the catalog loads / changes.
  const customCatalog = useCustomActivityCatalogStore((s) => s.catalog);
  // Rebuild categories on language change so labels follow the current language.
  const filteredCategories = useMemo(() => buildActivityCategories().map((cat) => ({
    ...cat,
    items: cat.items.filter((item) =>
      item.label.toLowerCase().includes(searchQuery.toLowerCase()) ||
      item.type.toLowerCase().includes(searchQuery.toLowerCase()),
    ),
  })).filter((cat) => cat.items.length > 0), [searchQuery, i18n.language, customCatalog]);

  if (fullscreen) return null;

  if (leftCollapsed) {
    return (
      <aside className="wd-dock wd-dock--rail bg-surface-low flex flex-col items-center shrink-0 z-10 border-r border-outline-variant/15 w-10 py-3 gap-2">
        <button
          onClick={() => setLeftCollapsed(false)}
          className="p-1.5 rounded hover:bg-surface-highest text-on-surface-variant transition-colors"
          title={t('editor:sidebarExpand')}
        >
          <SidePanelOpen size={16} />
        </button>
        <button
          onClick={() => { setLeftTab('workflows'); setLeftCollapsed(false); }}
          className="p-1.5 rounded hover:bg-surface-highest text-on-surface-variant transition-colors"
          title={t('nav:workflows')}
        >
          <FolderTree size={16} />
        </button>
        <button
          onClick={() => { setLeftTab('nodes'); setLeftCollapsed(false); }}
          className="p-1.5 rounded hover:bg-surface-highest text-on-surface-variant transition-colors"
          title={t('editor:library.title')}
        >
          <Apps size={16} />
        </button>
      </aside>
    );
  }

  return (
    <>
      <aside className="wd-dock bg-surface-low flex flex-col shrink-0 z-10 border-r border-outline-variant/15 relative" style={{ width: panelSize }}>
        {/* Tab bar */}
        <div className="flex items-center border-b border-outline-variant/15 shrink-0">
          <button
            onClick={() => setLeftTab('workflows')}
            className={`flex items-center gap-1.5 px-3 py-2 text-[11px] font-label font-semibold transition-colors border-b-2 ${
              leftTab === 'workflows'
                ? 'text-on-surface border-primary'
                : 'text-on-surface-variant border-transparent hover:text-on-surface'
            }`}
          >
            <FolderTree size={12} />
            {t('editor:tabWorkflows')}
          </button>
          <button
            onClick={() => setLeftTab('nodes')}
            className={`flex items-center gap-1.5 px-3 py-2 text-[11px] font-label font-semibold transition-colors border-b-2 ${
              leftTab === 'nodes'
                ? 'text-on-surface border-primary'
                : 'text-on-surface-variant border-transparent hover:text-on-surface'
            }`}
          >
            <Apps size={12} />
            {t('editor:tabNodes')}
          </button>
          <button
            onClick={() => setLeftCollapsed(true)}
            className="ml-auto mr-1 p-1.5 rounded text-on-surface-variant hover:bg-surface-highest transition-colors"
            title={t('editor:sidebarCollapse')}
          >
            <SidePanelClose size={14} />
          </button>
        </div>

        {leftTab === 'nodes' && (
          <>
            <div className="p-5 pb-2">
              <div className="relative">
                <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant" />
                <input
                  type="text"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="w-full bg-surface-high hover:bg-surface-highest focus:bg-surface-container border border-transparent focus:border-outline-variant/40 rounded-md py-1.5 pl-9 pr-4 text-xs font-label transition-all placeholder:text-outline focus:outline-none"
                  placeholder={t('editor:library.search')}
                />
              </div>
            </div>
            <div className="flex-1 overflow-y-auto px-3 py-2 space-y-0.5">
              {filteredCategories.map((cat) => {
                if (cat.items.length === 0) return null;
                // Force-expand while filtering — otherwise a match would disappear under a
                // collapsed header and the user would think the search found nothing.
                const isCollapsed = !searchQuery && !!collapsedCategories[cat.name];
                return (
                  <div key={cat.name}>
                    <button
                      type="button"
                      onClick={() => toggleCategory(cat.name)}
                      className="flex items-center gap-1 w-full px-2 py-1 rounded hover:bg-surface-highest/50 transition-colors group"
                      aria-expanded={!isCollapsed}
                    >
                      <ChevronDown
                        size={14}
                        className="text-on-surface-variant shrink-0 transition-transform"
                        style={{ transform: isCollapsed ? 'rotate(-90deg)' : 'rotate(0deg)' }}
                        aria-hidden="true"
                      />
                      <h3 className="font-label text-[10px] font-bold text-on-surface-variant uppercase tracking-widest">
                        {cat.name}
                      </h3>
                      <span className="ml-auto text-[9px] font-label text-outline tabular-nums">
                        {cat.items.length}
                      </span>
                    </button>
                    {!isCollapsed && (
                      <div className="mt-0.5">
                        {cat.items.map((item) => (
                          <button
                            key={item.type}
                            onClick={canWrite ? () => addNode(item.type, item.label) : undefined}
                            draggable={canWrite}
                            onDragStart={canWrite ? (e) => {
                              e.dataTransfer.setData('application/nodepilot-activity', JSON.stringify({ type: item.type, label: item.label }));
                              e.dataTransfer.effectAllowed = 'copy';
                            } : undefined}
                            disabled={!canWrite}
                            title={canWrite ? undefined : t('editor:productiveBanner')}
                            className={`flex items-center gap-3 w-full px-3 py-0.5 rounded-md transition-colors text-left ${
                              canWrite
                                ? 'hover:bg-surface-highest cursor-grab'
                                : 'opacity-50 cursor-not-allowed'
                            }`}
                          >
                            <ActivityIcon type={item.type} />
                            <span className="font-label text-xs font-medium text-on-surface">{item.label}</span>
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}

              {/* Snippets — predefined mini-patterns (Try-Catch, ForEach, Fan-out+Join,
                  HTTP-Retry). Clicking inserts the whole group onto the canvas with freshly
                  generated IDs. Same collapsible-category pattern as the activities list. */}
              <SnippetsSection
                collapsed={!!collapsedCategories['__snippets']}
                onToggle={() => toggleCategory('__snippets')}
                onInsert={addSnippet}
                canWrite={canWrite}
              />
            </div>
          </>
        )}

        {leftTab === 'workflows' && (
          <WorkflowBrowser
            currentWorkflowId={workflowId}
            canEmbed={isStartWorkflowSelected}
            onOpen={onOpenWorkflow}
            onEmbed={onEmbedWorkflow}
          />
        )}
      </aside>
      <ResizeHandle direction="horizontal" {...panelHandleProps} />
    </>
  );
}
