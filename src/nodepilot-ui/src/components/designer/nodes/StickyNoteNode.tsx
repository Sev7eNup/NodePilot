import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { type NodeProps, useReactFlow, useStore, NodeResizer, type Node } from '@xyflow/react';
import { useThemeStore } from '../../../stores/themeStore';

/**
 * Pure annotation node — documents the workflow inline (why does this branch exist, what does
 * the following RunScript do, TODO notes). Never executed by the engine:
 *
 *   - No source/target handles → it isn't an edge endpoint, so it can't be part of the graph
 *     traversal.
 *   - `data.disabled = true` is set on creation, so even if an export later wires edges to this
 *     node, the engine sorts it into `disabledNodeIds` and skips it (see
 *     WorkflowEngine.ExecuteAsync).
 *
 * Editing UX:
 *   - Double-click opens an inline textarea with autofocus + full text selection
 *   - Blur or Ctrl/Cmd+Enter commits the value back to `data.text` via React Flow's
 *     <c>setNodes</c>
 *   - Escape discards the change
 *
 * Styling: sunny yellow like a physical sticky note — stands out clearly from activity nodes
 * so the reader immediately sees "this is a comment, not code".
 */
// Five preset sizes — covers headline / standard / callout without the user having to fiddle
// with exact pixel values. In px; line-height is derived in the render via leading-snug.
const FONT_SIZE_STEPS = [11, 13, 16, 20, 28] as const;
const DEFAULT_FONT_SIZE = 13;

// Minimum on-screen font size (px) guaranteed on hover. If the canvas is zoomed out far enough
// that `fontSize * zoom` falls below this, the note scales up on hover just enough to reach
// this threshold again. At a zoom near 1 (or zoomed in), the computed factor is ≤ 1 → we clamp
// to 1, so the note stays unchanged.
const HOVER_READABLE_PX = 14;

export function StickyNoteNode({ id, data, selected }: NodeProps) {
  const { t } = useTranslation('designer');
  const d = data as Record<string, unknown>;
  const savedText = (d.text as string) ?? (d.label as string) ?? '';
  const fontSize = typeof d.fontSize === 'number' ? (d.fontSize as number) : DEFAULT_FONT_SIZE;
  const { setNodes } = useReactFlow();
  const isDark = useThemeStore((s) => s.resolvedTheme === 'dark');

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(savedText);
  const [hovered, setHovered] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Current viewport zoom (canvas scale). React Flow stores this in its store as
  // `transform: [tx, ty, zoom]`. We subscribe with a selector, so the note only re-renders
  // when zoom changes — not on pan or selection updates.
  const zoom = useStore((s) => s.transform[2]);

  // Explicit width from the node. NodeResizer in @xyflow/react v12 writes the new dimensions
  // via a dimensions-change event onto the TOP-LEVEL field `node.width` / `node.height`
  // (`element.width = change.dimensions.width`) — NOT into `node.style`. So we check
  // `node.width` first, with `node.style?.width` as a fallback for our own defaults set in
  // useNodeOperations. As soon as the user drags a resize handle, this detection flips to
  // `true` and the inner div switches to "fill parent".
  const explicitWidth = useStore((s) => {
    const n = s.nodeLookup.get(id);
    return n?.width ?? n?.style?.width;
  });
  const isResizable = explicitWidth !== undefined;

  // Hover scaling: only kicks in while the user is actually hovering the note AND the canvas
  // zoom would otherwise make the text unreadable. `Math.max(1, …)` clamps from below — so
  // zooming in never shrinks the note "backwards". We disable the effect while editing,
  // otherwise the textarea would jitter under the cursor. Also disabled while the note is
  // selected (= resize handles active), otherwise the handles would sit offset from the note's
  // actual geometry.
  const hoverScale = (hovered && !editing && !selected)
    ? Math.max(1, HOVER_READABLE_PX / (fontSize * zoom))
    : 1;
  const isHoverZooming = hoverScale > 1;

  // If the sticky note was changed from outside (undo, paste), sync the local draft to match
  // — as long as we're not currently editing.
  useEffect(() => {
    if (!editing) setDraft(savedText);
  }, [savedText, editing]);

  // Autofocus + full text selection when opening — so the user can start typing right away
  // instead of manually deleting the placeholder.
  useEffect(() => {
    if (!editing) return;
    requestAnimationFrame(() => {
      textareaRef.current?.focus();
      textareaRef.current?.select();
    });
  }, [editing]);

  const commit = () => {
    setEditing(false);
    if (draft === savedText) return;
    setNodes((nds: Node[]) => nds.map((n) => n.id === id
      ? { ...n, data: { ...(n.data as Record<string, unknown>), text: draft } }
      : n,
    ));
  };

  const cancel = () => {
    setDraft(savedText);
    setEditing(false);
  };

  /** Steps through the size presets one at a time. +1 / -1 per click, clamped to the array's
   *  bounds so it never produces undefined. */
  const stepFontSize = (delta: 1 | -1) => {
    // If the saved value isn't one of the presets (e.g. from an old import), jump to the next
    // preset in the direction of the change instead of stumbling over the index.
    const idx = FONT_SIZE_STEPS.indexOf(fontSize as typeof FONT_SIZE_STEPS[number]);
    const currentIdx = idx >= 0
      ? idx
      : (delta > 0
          ? FONT_SIZE_STEPS.findIndex((s) => s > fontSize)
          : FONT_SIZE_STEPS.some((s) => s < fontSize)
            ? FONT_SIZE_STEPS.findLastIndex((s) => s < fontSize)
            : 0);
    const nextIdx = Math.max(0, Math.min(FONT_SIZE_STEPS.length - 1, currentIdx + delta));
    const next = FONT_SIZE_STEPS[nextIdx];
    if (next === fontSize) return;
    setNodes((nds: Node[]) => nds.map((n) => n.id === id
      ? { ...n, data: { ...(n.data as Record<string, unknown>), fontSize: next } }
      : n,
    ));
  };

  const displayText = savedText || t('nodes.stickyNote.emptyHint');
  const isPlaceholder = !savedText;

  const canShrink = fontSize > FONT_SIZE_STEPS[0];
  const canGrow = fontSize < (FONT_SIZE_STEPS.at(-1) ?? 0);

  return (
    <>
      {/* NodeResizer draws the eight drag handles on selection. minWidth/minHeight prevent
          the note from being shrunk down to unreadability; while editing we hide the handles
          so a drag doesn't accidentally interrupt the textarea's text selection. */}
      <NodeResizer
        minWidth={140}
        minHeight={50}
        isVisible={selected && !editing}
        lineStyle={{ borderColor: isDark ? 'rgba(251,191,36,0.40)' : 'rgba(202,138,4,0.55)' }}
        handleStyle={{ width: 8, height: 8, borderRadius: 2, background: isDark ? '#92400e' : '#ca8a04' }}
      />
      <div
        className={`relative rounded-lg font-label leading-snug whitespace-pre-wrap break-words flex flex-col ${
          isDark
            ? (selected ? 'ring-2 ring-amber-400/55' : 'ring-1 ring-amber-400/18')
            : (selected ? 'ring-2 ring-yellow-400' : 'ring-1 ring-yellow-300/60')
        }`}
        style={{
          backgroundColor: isDark ? 'rgba(20, 16, 4, 0.92)' : '#fef9c3',
          backgroundImage: isDark
            ? 'linear-gradient(180deg, rgba(251,191,36,.10) 0%, rgba(251,191,36,.02) 45%, transparent 100%)'
            : undefined,
          color: isDark ? '#fde68a' : '#713f12',
          width: isResizable ? '100%' : undefined,
          height: isResizable ? '100%' : undefined,
          minWidth: isResizable ? undefined : 160,
          maxWidth: isResizable ? undefined : 280,
          fontSize: `${fontSize}px`,
          transform: `scale(${hoverScale})`,
          transformOrigin: 'center',
          transition: 'transform 120ms ease-out, box-shadow 120ms ease-out',
          zIndex: isHoverZooming ? 50 : undefined,
          boxShadow: isDark
            ? (isHoverZooming
                ? '0 12px 36px rgba(0,0,0,.58), 0 2px 8px rgba(0,0,0,.48), inset 0 1px 0 rgba(251,191,36,.14)'
                : '0 2px 6px rgba(0,0,0,.48), 0 8px 22px rgba(0,0,0,.38), inset 0 1px 0 rgba(251,191,36,.10), inset 0 -1px 0 rgba(0,0,0,.42)')
            : (isHoverZooming ? '0 10px 30px rgba(202,138,4,0.32)' : '0 6px 20px rgba(202,138,4,0.18)'),
        }}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        onDoubleClick={(e) => {
          e.stopPropagation();
          setEditing(true);
        }}
      >
      {/* Font-size stepper — only visible when the note is selected, so the canvas doesn't get
          cluttered with control chrome when there are many notes. `nodrag` makes sure clicks
          on the buttons aren't interpreted as a drag on the node. */}
      {selected && !editing && (
        <div
          className={`nodrag absolute -top-2.5 right-2 flex items-center gap-0.5 rounded-full border shadow-sm px-1 py-0.5 ${
            isDark ? 'bg-amber-950/90 border-amber-500/30' : 'bg-yellow-200/95 border-yellow-400/70'
          }`}
          style={{ transform: 'rotate(0.5deg)' /* cancels out the note's 0.5° tilt */ }}
        >
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); stepFontSize(-1); }}
            disabled={!canShrink}
            className={`flex items-center justify-center w-5 h-5 rounded-full disabled:opacity-30 disabled:cursor-not-allowed leading-none ${
              isDark ? 'text-amber-300 hover:bg-amber-800/60' : 'text-yellow-900 hover:bg-yellow-300/80'
            }`}
            title={t('nodes.stickyNote.smaller')}
          >
            <span style={{ fontSize: '10px', fontWeight: 600 }}>A−</span>
          </button>
          <span className={`font-mono text-[9px] tabular-nums w-5 text-center ${isDark ? 'text-amber-400' : 'text-yellow-800'}`} title={t('nodes.stickyNote.currentFontSize')}>
            {fontSize}
          </span>
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); stepFontSize(1); }}
            disabled={!canGrow}
            className={`flex items-center justify-center w-5 h-5 rounded-full disabled:opacity-30 disabled:cursor-not-allowed leading-none ${
              isDark ? 'text-amber-300 hover:bg-amber-800/60' : 'text-yellow-900 hover:bg-yellow-300/80'
            }`}
            title={t('nodes.stickyNote.larger')}
          >
            <span style={{ fontSize: '12px', fontWeight: 700 }}>A+</span>
          </button>
        </div>
      )}

      {/* Scroll container: if the user shrinks the note smaller than the text needs, the
          content stays within the frame and becomes vertically scrollable. `min-h-0` is
          required — otherwise flex's default min-height: auto overrides the size constraint
          and scrolling never kicks in. Padding moves here from the outer div, so the
          scrollbar sits at the note's outer edge. */}
      <div className="nowheel flex-1 min-h-0 overflow-y-auto overflow-x-hidden px-3 py-2.5">
        {editing ? (
          // `nodrag` + `nopan` prevent React Flow from interpreting a mouse drag inside the
          // textarea as a node drag / canvas pan — otherwise text selection wouldn't work.
          // `h-full` so the textarea fills the whole scrollable area; its own native
          // scrollbar then handles overflow while typing.
          <textarea
            ref={textareaRef}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={commit}
            onKeyDown={(e) => {
              if (e.key === 'Escape') { e.preventDefault(); cancel(); }
              else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); commit(); }
            }}
            className={`nodrag nopan w-full h-full min-h-[60px] bg-transparent outline-none resize-none font-label leading-snug ${
              isDark ? 'text-amber-200 placeholder:text-amber-500/50' : 'text-yellow-900 placeholder:text-yellow-700/60'
            }`}
            style={{ fontSize: `${fontSize}px` }}
            placeholder={t('nodes.stickyNote.placeholder')}
          />
        ) : (
          <span className={`${isPlaceholder ? 'italic opacity-60' : ''} ${isDark && isPlaceholder ? 'text-amber-500' : ''}`}>{displayText}</span>
        )}
      </div>
      </div>
    </>
  );
}
