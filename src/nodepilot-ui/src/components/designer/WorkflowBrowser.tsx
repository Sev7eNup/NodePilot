import { ChevronDown, ChevronRight, FlashFilled, Folder, Launch, Link, Search } from '@carbon/icons-react';
import { useCallback, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';
import type { Workflow } from '../../types/api';
import { useWorkflowBrowserStore } from '../../stores/workflowBrowserStore';
import { SharedFolderTree, WORKFLOW_DRAG_MIME } from '../workflows/SharedFolderTree';
import { sharedFoldersApi } from '../../api/sharedFolders';
import { TRIGGER_META } from './workflowTriggerMeta';
import { WorkflowInfoCard } from './WorkflowInfoCard';

const GROUP_ORDER = [
  ...ACTIVITY_CATALOG.filter((activity) => activity.category === 'trigger').map((activity) => activity.type),
  '__none__'
];

// In folder view the flat workflow list is sized to show exactly 10 rows; any extra rows
// scroll. Rows are pinned to a fixed 20px (h-5) so the math is exact:
// 10 rows × 20px + 9 gaps × 2px (space-y-0.5) + 16px list padding (py-2) = 234px.
export const WORKFLOW_LIST_HEIGHT_PX = 10 * 20 + 9 * 2 + 16;

interface Props {
  /** id of the currently-open workflow — excluded from the list to avoid recursive selection */
  currentWorkflowId?: string;
  /** callback when user clicks "Open" on a workflow (navigate to its designer) */
  onOpen: (workflow: Workflow) => void;
  /** optional: if a startWorkflow step is selected, this is the id it targets right now. Enables the "Insert here" affordance. */
  canEmbed: boolean;
  onEmbed?: (workflow: Workflow) => void;
}

/**
 * Right-side companion panel in the editor's left sidebar. Lists all workflows
 * grouped either by primary trigger type or by the org-level shared folder tree.
 */
export function WorkflowBrowser({ currentWorkflowId, onOpen, canEmbed, onEmbed }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const queryClient = useQueryClient();

  const { data: workflows = [], isLoading } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
  });


  const [query, setQuery] = useState('');
  const [selectedFolderId, setSelectedFolderId] = useState<string | null>(null);
  const [hoveredWorkflow, setHoveredWorkflow] = useState<Workflow | null>(null);
  const { viewMode, setViewMode, collapsedFolders, toggleFolder, infoCardHeight, setInfoCardHeight } = useWorkflowBrowserStore();

  // Splitter between the workflow list and the info card. The card sits *below* the splitter,
  // so dragging down must shrink the card (and grow the list) — hence `height - delta`.
  const infoDragStartRef = useRef<{ y: number; height: number } | null>(null);
  const handleInfoDividerMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    infoDragStartRef.current = { y: e.clientY, height: infoCardHeight };
    const onMouseMove = (ev: MouseEvent) => {
      if (!infoDragStartRef.current) return;
      const delta = ev.clientY - infoDragStartRef.current.y;
      setInfoCardHeight(Math.max(80, Math.min(480, infoDragStartRef.current.height - delta)));
    };
    const onMouseUp = () => {
      infoDragStartRef.current = null;
      globalThis.removeEventListener('mousemove', onMouseMove);
      globalThis.removeEventListener('mouseup', onMouseUp);
    };
    globalThis.addEventListener('mousemove', onMouseMove);
    globalThis.addEventListener('mouseup', onMouseUp);
  }, [infoCardHeight, setInfoCardHeight]);

  // Exclude current workflow in embed-picker mode; apply text search.
  const baseFiltered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return workflows.filter((w) => {
      if (canEmbed && w.id === currentWorkflowId) return false;
      if (!q) return true;
      return w.name.toLowerCase().includes(q) || (w.description ?? '').toLowerCase().includes(q);
    });
  }, [workflows, query, canEmbed, currentWorkflowId]);

  // In folder mode: when a folder is selected, narrow to that folder + descendants
  // via path prefix (same logic as WorkflowsPage).
  const filteredByFolder = useMemo(() => {
    if (!selectedFolderId) return baseFiltered;
    return baseFiltered.filter((w) => w.folderId === selectedFolderId);
  }, [baseFiltered, selectedFolderId]);

  // Trigger-mode grouping (unchanged from previous implementation).
  const grouped = useMemo(() => {
    const byGroup = new Map<string, Workflow[]>();
    for (const w of baseFiltered) {
      const primary = w.triggerTypes?.[0] ?? '__none__';
      const key = TRIGGER_META[primary] ? primary : '__none__';
      if (!byGroup.has(key)) byGroup.set(key, []);
      byGroup.get(key)!.push(w);
    }
    for (const list of byGroup.values()) list.sort((a, b) => a.name.localeCompare(b.name));
    return GROUP_ORDER
      .map((k) => ({ key: k, items: byGroup.get(k) ?? [] }))
      .filter((g) => g.items.length > 0);
  }, [baseFiltered]);

  // Move a workflow to a shared folder. SharedFolderTree only fires this when the
  // destination folder has canEdit; we also gate on the source workflow's canEdit so
  // a Viewer can't drag-initiate a workflow they're not allowed to move.
  const handleWorkflowDropped = useCallback(
    (workflowId: string, folderId: string) => {
      sharedFoldersApi.moveWorkflowToFolder(workflowId, folderId).then(() => {
        queryClient.invalidateQueries({ queryKey: ['workflows'] });
        queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
      });
    },
    [queryClient],
  );

  const totalShown = filteredByFolder.length;

  // The info card describes whatever the user is hovering; when nothing is hovered it falls
  // back to the currently-open workflow so the panel is never empty. Derived from the shared
  // `workflows` array, so it works identically in both folder and trigger view.
  const cardWorkflow = hoveredWorkflow ?? workflows.find((w) => w.id === currentWorkflowId) ?? null;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <div className="p-5 pb-2 shrink-0">
        {/* View-mode toggle */}
        <div className="flex items-center bg-surface-high rounded-md p-0.5 mb-3">
          <button
            onClick={() => setViewMode('folder')}
            className={`flex-1 flex items-center justify-center gap-1.5 px-2 py-1 rounded text-[11px] font-label font-semibold transition-colors ${
              viewMode === 'folder' ? 'bg-surface-lowest text-on-surface shadow-sm' : 'text-on-surface-variant hover:text-on-surface'
            }`}
          >
            <Folder size={11} /> {t('browserFolderView')}
          </button>
          <button
            onClick={() => setViewMode('trigger')}
            className={`flex-1 flex items-center justify-center gap-1.5 px-2 py-1 rounded text-[11px] font-label font-semibold transition-colors ${
              viewMode === 'trigger' ? 'bg-surface-lowest text-on-surface shadow-sm' : 'text-on-surface-variant hover:text-on-surface'
            }`}
          >
            <FlashFilled size={11} /> {t('browserTriggerView')}
          </button>
        </div>

        <div className="relative">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant" />
          <input
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            className="w-full bg-surface-high hover:bg-surface-highest focus:bg-surface-container border border-transparent focus:border-outline-variant/40 rounded-md py-1.5 pl-9 pr-4 text-xs font-label transition-all placeholder:text-outline focus:outline-none"
            placeholder={t('browser.searchPlaceholder')}
          />
        </div>
      </div>
      {/* Shared folder tree — grows to take the space freed by the fixed-height list */}
      {viewMode === 'folder' && (
        <div className="flex-1 min-h-0 overflow-y-auto">
          <SharedFolderTree
            selectedFolderId={selectedFolderId}
            onFolderSelected={setSelectedFolderId}
            onWorkflowDropped={handleWorkflowDropped}
            compact
          />
        </div>
      )}
      {/* Flat workflow list. Folder view: fixed height for exactly 10 rows (rest scroll), so
          the freed space goes to the folder tree above. Trigger view (no tree): flex-fill. */}
      <div
        data-testid="workflow-list"
        className={`overflow-y-auto px-3 py-2 space-y-0.5 ${viewMode === 'folder' ? 'shrink-0' : 'flex-1 min-h-0'}`}
        style={viewMode === 'folder' ? { height: WORKFLOW_LIST_HEIGHT_PX } : undefined}
      >
        {isLoading && (
          <div className="text-[11px] font-label text-on-surface-variant px-2">Loading…</div>
        )}

        {/* Nach Trigger view */}
        {viewMode === 'trigger' && grouped.length === 0 && !isLoading && (
          <div className="text-[11px] font-label text-on-surface-variant px-2 py-4 text-center">
            {query ? 'Keine Treffer.' : 'Keine anderen Workflows vorhanden.'}
          </div>
        )}
        {viewMode === 'trigger' && grouped.map((g) => {
          const meta = TRIGGER_META[g.key];
          const Icon = meta?.icon ?? FlashFilled;
          const label = meta ? t(meta.labelKey) : t('browser.noTrigger');
          const isCollapsed = collapsedFolders[g.key] ?? true;
          return (
            <div key={g.key}>
              <button
                onClick={() => toggleFolder(g.key)}
                className="w-full flex items-center gap-1.5 px-2 py-1 text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest hover:text-on-surface transition-colors"
              >
                {isCollapsed ? <ChevronRight size={11} /> : <ChevronDown size={11} />}
                <Icon size={11} className={meta?.color} />
                <span>{label}</span>
                <span className="ml-auto text-outline font-normal">{g.items.length}</span>
              </button>
              {!isCollapsed && (
                <div className="mt-0.5">
                  {g.items.map((w) => (
                    <WorkflowBrowserItem
                      key={w.id}
                      workflow={w}
                      canEmbed={canEmbed}
                      isCurrent={w.id === currentWorkflowId}
                      onOpen={() => onOpen(w)}
                      onEmbed={onEmbed ? () => onEmbed(w) : undefined}
                      onHover={setHoveredWorkflow}
                    />
                  ))}
                </div>
              )}
            </div>
          );
        })}

        {/* Ordner view — flat list filtered by selected folder */}
        {viewMode === 'folder' && !isLoading && totalShown === 0 && (
          <div className="text-[11px] font-label text-on-surface-variant px-2 py-4 text-center">
            {query
              ? 'Keine Treffer.'
              : selectedFolderId
              ? 'Keine Workflows in diesem Ordner.'
              : 'Keine anderen Workflows vorhanden.'}
          </div>
        )}
        {viewMode === 'folder' && filteredByFolder.map((w) => (
          <WorkflowBrowserItem
            key={w.id}
            workflow={w}
            canEmbed={canEmbed}
            isCurrent={w.id === currentWorkflowId}
            onOpen={() => onOpen(w)}
            onEmbed={onEmbed ? () => onEmbed(w) : undefined}
            onHover={setHoveredWorkflow}
            draggable={w.capabilities?.canEdit !== false}
          />
        ))}
      </div>
      {/* Splitter between list and info card */}
      <div
        onMouseDown={handleInfoDividerMouseDown}
        className="shrink-0 h-2 flex items-center justify-center cursor-row-resize hover:bg-primary/10 active:bg-primary/20 transition-colors group border-y border-outline-variant/20 select-none"
      >
        <div className="w-8 h-0.5 rounded-full bg-outline-variant group-hover:bg-primary/50 transition-colors" />
      </div>
      {/* Workflow-details card — describes the hovered (fallback: open) workflow */}
      <div className="shrink-0 overflow-y-auto" style={{ height: infoCardHeight }}>
        <WorkflowInfoCard workflow={cardWorkflow} />
      </div>
    </div>
  );
}

function WorkflowBrowserItem({
  workflow, canEmbed, isCurrent, onOpen, onEmbed, onHover, draggable = false,
}: Readonly<{
  workflow: Workflow;
  canEmbed: boolean;
  isCurrent: boolean;
  onOpen: () => void;
  onEmbed?: () => void;
  /** Lifts the hovered workflow up so the info card can describe it. */
  onHover?: (w: Workflow | null) => void;
  draggable?: boolean;
}>) {
  const [hover, setHover] = useState(false);
  const primaryAction = canEmbed && onEmbed ? onEmbed : onOpen;

  // "You are here" rendering: non-clickable marker for the currently-open workflow. Still
  // hover-tracked so the info card updates when the user mouses over the open workflow.
  if (isCurrent) {
    return (
      <div
        onMouseEnter={() => onHover?.(workflow)}
        onMouseLeave={() => onHover?.(null)}
        className="flex items-center gap-2 w-full px-3 h-5 rounded-md bg-primary-fixed/40 border border-primary/30 text-left select-none"
        title="Aktuell geöffneter Workflow"
      >
        <span
          className={`inline-block w-1.5 h-1.5 rounded-full shrink-0 ${
            workflow.isEnabled ? 'bg-emerald-500' : 'bg-outline'
          }`}
        />
        <span className="font-label text-xs font-semibold text-primary truncate flex-1">
          {workflow.name}
        </span>
        <span className="text-[9px] font-label text-primary/70 uppercase tracking-wider font-semibold">
          current
        </span>
      </div>
    );
  }

  return (
    <div
      onMouseEnter={() => { setHover(true); onHover?.(workflow); }}
      onMouseLeave={() => { setHover(false); onHover?.(null); }}
      draggable={draggable}
      onDragStart={draggable ? (e) => {
        e.dataTransfer.setData(WORKFLOW_DRAG_MIME, workflow.id);
        e.dataTransfer.effectAllowed = 'move';
      } : undefined}
      className="relative"
    >
      <button
        onClick={primaryAction}
        className="group flex items-center gap-2 w-full px-3 h-5 rounded-md hover:bg-surface-highest transition-colors text-left"
        title={workflow.description ?? workflow.name}
      >
        <span
          className={`inline-block w-1.5 h-1.5 rounded-full shrink-0 ${
            workflow.isEnabled ? 'bg-emerald-500' : 'bg-outline'
          }`}
          aria-label={workflow.isEnabled ? 'Enabled' : 'Disabled'}
        />
        <span className="font-label text-xs font-medium text-on-surface truncate flex-1">
          {workflow.name}
        </span>
        {workflow.activityCount != null && (
          <span className="text-[9px] font-label text-outline tabular-nums">
            {workflow.activityCount}
          </span>
        )}
      </button>
      {hover && (
        <div className="absolute right-1 top-1/2 -translate-y-1/2 flex items-center gap-0.5 bg-surface-lowest/95 backdrop-blur rounded shadow-sm border border-outline-variant/30 px-0.5">
          {canEmbed && onEmbed && (
            <button
              onClick={(e) => { e.stopPropagation(); onEmbed(); }}
              className="p-1 text-primary hover:bg-primary-fixed rounded transition-colors"
              title="In Start-Workflow-Step einsetzen"
            >
              <Link size={12} />
            </button>
          )}
          <button
            onClick={(e) => { e.stopPropagation(); onOpen(); }}
            className="p-1 text-on-surface-variant hover:bg-surface-high rounded transition-colors"
            title="Workflow öffnen"
          >
            <Launch size={12} />
          </button>
        </div>
      )}
    </div>
  );
}
