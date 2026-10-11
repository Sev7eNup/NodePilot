import { Maximize, Minimize } from '@carbon/icons-react';
import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { type NodeProps, type Node, NodeResizer, useReactFlow, useStore } from '@xyflow/react';
import { getCollapsedGroupSize, getExpandedGroupSize } from '../../../lib/collapsedGraphView';
import { groupLabelFontSize } from '../../../lib/groupLabel';

type GroupColor = typeof COLORS[number];

// Group chrome (see `.np-group-*` in index.css): a quiet tinted frame with a label chip.
// The hue is user-picked data (9 fixed colors) resolved through `--np-group-<color>` tokens.
const COLORS = ['blue', 'teal', 'green', 'amber', 'orange', 'rose', 'pink', 'violet', 'slate'] as const;

export const GroupNodeEditContext = createContext<{
  updateGroupNode: (id: string, updater: (node: Node) => Node) => void;
} | null>(null);

/**
 * Id of the group currently highlighted as the drag drop-target, or null. Set by the editor during
 * a node drag (see WorkflowEditorPage.onNodeDrag). Default null so the read-only preview renderer,
 * which mounts GroupNode without a provider, simply never highlights.
 */
export const GroupDropTargetContext = createContext<string | null>(null);

/**
 * Purely visual grouping node — no handles, never executed, annotation only.
 * Child nodes move together with it via React Flow's parentId mechanism.
 * data.disabled=true makes sure the engine skips this node.
 */
export function GroupNode({ id, data, selected }: NodeProps) {
  const { t } = useTranslation('designer');
  const d = data as Record<string, unknown>;
  const savedLabel = (d.label as string) ?? 'Group';
  const color: GroupColor = COLORS.includes(d.color as GroupColor) ? (d.color as GroupColor) : 'blue';
  const collapsed = d.collapsed === true;
  const childCount = typeof d.__collapsedChildCount === 'number' ? d.__collapsedChildCount : 0;
  const { setNodes } = useReactFlow();
  const editContext = useContext(GroupNodeEditContext);
  const isDropTarget = useContext(GroupDropTargetContext) === id;
  // Zoom-aware label size: grows when zooming in, stays readable when zooming out. Subscribed via
  // selector so the node only re-renders on zoom change, not on pan/selection.
  const zoom = useStore((s) => s.transform[2]);
  const labelFontSize = groupLabelFontSize(zoom);

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(savedLabel);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { if (!editing) setDraft(savedLabel); }, [savedLabel, editing]);

  useEffect(() => {
    if (!editing) return;
    requestAnimationFrame(() => { inputRef.current?.focus(); inputRef.current?.select(); });
  }, [editing]);

  const updateGroupNode = useCallback((updater: (node: Node) => Node) => {
    if (editContext) {
      editContext.updateGroupNode(id, updater);
      return;
    }
    setNodes((nds: Node[]) => nds.map((n) => n.id === id ? updater(n) : n));
  }, [editContext, id, setNodes]);

  const commit = () => {
    setEditing(false);
    const trimmed = draft.trim() || 'Group';
    if (trimmed === savedLabel) return;
    updateGroupNode((n) => ({
      ...n,
      data: { ...withoutRuntimeGroupData(n.data as Record<string, unknown>), label: trimmed },
    }));
  };

  const setColor = (c: GroupColor) => {
    updateGroupNode((n) => ({
      ...n,
      data: { ...withoutRuntimeGroupData(n.data as Record<string, unknown>), color: c },
    }));
  };

  const toggleCollapsed = () => {
    updateGroupNode((n) => {
      const data = withoutRuntimeGroupData((n.data as Record<string, unknown>) ?? {});
      if (collapsed) {
        const expandedSize = getExpandedGroupSize(n);
        return {
          ...n,
          style: { ...n.style, width: expandedSize.width, height: expandedSize.height },
          data: { ...data, collapsed: false },
        };
      }
      const currentWidth = typeof n.style?.width === 'number' ? n.style.width : n.measured?.width ?? 360;
      const currentHeight = typeof n.style?.height === 'number' ? n.style.height : n.measured?.height ?? 220;
      const collapsedSize = getCollapsedGroupSize();
      return {
        ...n,
        style: { ...n.style, width: collapsedSize.width, height: collapsedSize.height },
        data: { ...data, collapsed: true, expandedSize: { width: currentWidth, height: currentHeight } },
      };
    });
  };

  return (
    <>
      <NodeResizer
        minWidth={200}
        minHeight={120}
        isVisible={selected && !collapsed}
        lineStyle={{ borderColor: `var(--np-group-${color})`, opacity: 0.6 }}
        handleStyle={{ width: 9, height: 9, borderRadius: '50%', background: `var(--np-group-${color})`, border: 'none' }}
      />
      <div
        className={`np-group-node w-full h-full overflow-hidden ${collapsed ? 'np-group-collapsed' : ''}`}
        data-color={color}
        data-drop-target={isDropTarget}
      >
        {/* Header: label chip on the left, actions on the right (shown on hover/selection). */}
        <div className="nodrag absolute top-0 left-0 right-0 flex items-center gap-2 px-3 py-2">
          <div className="np-group-chip">
            {editing ? (
              <input
                ref={inputRef}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onBlur={commit}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') { e.preventDefault(); commit(); }
                  if (e.key === 'Escape') { e.preventDefault(); setDraft(savedLabel); setEditing(false); }
                }}
                className="nopan min-w-0 bg-transparent outline-none font-semibold font-headline text-left"
                style={{ maxWidth: 240, fontSize: `${labelFontSize}px` }}
              />
            ) : (
              <span
                className="min-w-0 truncate font-semibold font-headline text-left cursor-text select-none"
                style={{ fontSize: `${labelFontSize}px` }}
                onDoubleClick={(e) => { e.stopPropagation(); setEditing(true); }}
                title={t('nodes.group.rename')}
              >
                {savedLabel}
              </span>
            )}
          </div>

          <div className="np-group-action ml-auto flex items-center gap-1 shrink-0">
            {/* Color swatches — only visible when selected */}
            {selected && !editing && COLORS.map((c) => (
              <button
                key={c}
                type="button"
                onClick={(e) => { e.stopPropagation(); setColor(c); }}
                className={`w-3 h-3 rounded-full ${c === color ? 'ring-2 ring-offset-1 ring-white/70' : 'opacity-60 hover:opacity-100'} transition-opacity`}
                style={{ background: `var(--np-group-${c})` }}
                title={c}
              />
            ))}
            {collapsed && childCount > 0 && (
              <span className="ml-1 rounded bg-surface-lowest/80 px-1.5 py-0.5 text-[10px] font-mono text-on-surface-variant">
                {childCount}
              </span>
            )}
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); toggleCollapsed(); }}
              className="ml-1 w-5 h-5 rounded-full flex items-center justify-center text-on-surface-variant hover:text-primary hover:bg-surface-lowest/70 transition-colors"
              title={collapsed ? t('nodes.group.expand') : t('nodes.group.collapse')}
            >
              {collapsed ? <Maximize size={12} /> : <Minimize size={12} />}
            </button>
          </div>
        </div>
        {collapsed && (
          <div className="absolute inset-x-3 bottom-2 text-[10px] font-label text-on-surface-variant truncate">
            {t('nodes.group.hiddenChildren')}
          </div>
        )}
      </div>
    </>
  );
}

function withoutRuntimeGroupData(data: Record<string, unknown>): Record<string, unknown> {
  const { __collapsedChildCount: _childCount, __expandedWidth: _width, __expandedHeight: _height, ...rest } = data;
  void _childCount;
  void _width;
  void _height;
  return rest;
}
