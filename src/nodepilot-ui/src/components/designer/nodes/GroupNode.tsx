import { Maximize, Minimize } from '@carbon/icons-react';
import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { type NodeProps, type Node, NodeResizer, useReactFlow, useStore } from '@xyflow/react';
import { getCollapsedGroupSize, getExpandedGroupSize } from '../../../lib/collapsedGraphView';
import { groupLabelFontSize } from '../../../lib/groupLabel';

type GroupColor = 'blue' | 'green' | 'amber' | 'rose' | 'slate';

// Soft-surface group chrome: a quiet solid hairline + faint color-wash fill and a
// tinted header bar replace the old dashed rectangle. Colors stay user-picked data
// (5 fixed hues), so they remain Tailwind palette classes rather than status tokens.
const COLOR_STYLES: Record<GroupColor, { border: string; bg: string; headerBg: string; text: string; swatch: string; ring: string; dropBg: string }> = {
  blue:  { border: 'border-blue-400/45',  bg: 'bg-blue-500/[0.05]',  headerBg: 'bg-blue-500/10',  text: 'text-blue-600 dark:text-blue-400',  swatch: 'bg-blue-500',  ring: 'ring-blue-400',  dropBg: 'bg-blue-100/25' },
  green: { border: 'border-green-400/45', bg: 'bg-green-500/[0.05]', headerBg: 'bg-green-500/10', text: 'text-green-600 dark:text-green-400', swatch: 'bg-green-500', ring: 'ring-green-400', dropBg: 'bg-green-100/25' },
  amber: { border: 'border-amber-400/45', bg: 'bg-amber-500/[0.05]', headerBg: 'bg-amber-500/10', text: 'text-amber-600 dark:text-amber-400', swatch: 'bg-amber-500', ring: 'ring-amber-400', dropBg: 'bg-amber-100/25' },
  rose:  { border: 'border-rose-400/45',  bg: 'bg-rose-500/[0.05]',  headerBg: 'bg-rose-500/10',  text: 'text-rose-600 dark:text-rose-400',  swatch: 'bg-rose-500',  ring: 'ring-rose-400',  dropBg: 'bg-rose-100/25' },
  slate: { border: 'border-slate-400/45', bg: 'bg-slate-500/[0.05]', headerBg: 'bg-slate-500/10', text: 'text-slate-500 dark:text-slate-400', swatch: 'bg-slate-500', ring: 'ring-slate-400', dropBg: 'bg-slate-100/25' },
};
const COLORS = Object.keys(COLOR_STYLES) as GroupColor[];

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
  const color: GroupColor = (d.color as GroupColor) ?? 'blue';
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

  const styles = COLOR_STYLES[color] ?? COLOR_STYLES.blue;

  return (
    <>
      <NodeResizer
        minWidth={200}
        minHeight={120}
        isVisible={selected && !collapsed}
        lineStyle={{ borderColor: 'rgba(148,163,184,0.6)' }}
        handleStyle={{ width: 8, height: 8, borderRadius: 2, background: '#94a3b8' }}
      />
      <div
        className={`w-full h-full rounded-xl transition-colors border overflow-hidden ${styles.border} ${
          isDropTarget ? `ring-2 ${styles.ring} ${styles.dropBg}` : styles.bg
        }`}
      >
        {/* Header bar — eine getönte Leiste über dem Gruppen-Inhalt. */}
        <div className={`nodrag absolute top-0 left-0 right-0 flex items-center gap-1.5 px-3 py-1.5 rounded-t-xl ${styles.headerBg} ${selected ? 'opacity-100' : 'opacity-90'}`}>
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
              className={`nopan flex-1 min-w-0 bg-transparent outline-none font-semibold font-headline ${styles.text}`}
              style={{ maxWidth: 240, fontSize: `${labelFontSize}px` }}
            />
          ) : (
            <span
              className={`flex-1 min-w-0 truncate font-semibold font-headline cursor-text select-none ${styles.text}`}
              style={{ fontSize: `${labelFontSize}px` }}
              onDoubleClick={(e) => { e.stopPropagation(); setEditing(true); }}
              title={t('nodes.group.rename')}
            >
              {savedLabel}
            </span>
          )}

          {/* Color swatches — only visible when selected */}
          {selected && !editing && (
            <div className="flex items-center gap-0.5 shrink-0">
              {COLORS.map((c) => (
                <button
                  key={c}
                  type="button"
                  onClick={(e) => { e.stopPropagation(); setColor(c); }}
                  className={`w-3 h-3 rounded-full ${COLOR_STYLES[c].swatch} ${c === color ? 'ring-2 ring-offset-1 ring-white/70' : 'opacity-60 hover:opacity-100'} transition-opacity`}
                  title={c}
                />
              ))}
            </div>
          )}
          {collapsed && childCount > 0 && (
            <span className="ml-1 rounded bg-surface-lowest/80 px-1.5 py-0.5 text-[10px] font-mono text-on-surface-variant">
              {childCount}
            </span>
          )}
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); toggleCollapsed(); }}
            className="ml-1 w-5 h-5 rounded flex items-center justify-center text-on-surface-variant hover:text-primary hover:bg-surface-lowest/70 transition-colors"
            title={collapsed ? t('nodes.group.expand') : t('nodes.group.collapse')}
          >
            {collapsed ? <Maximize size={12} /> : <Minimize size={12} />}
          </button>
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
