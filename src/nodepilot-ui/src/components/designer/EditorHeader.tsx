import { useDesignStore } from '../../stores/designStore';
import { CompactEditorHeader } from './header/CompactEditorHeader';
import { ClassicEditorHeader } from './header/ClassicEditorHeader';
import type { EditorHeaderProps } from './header/editorHeaderTypes';

export type { EditorHeaderProps } from './header/editorHeaderTypes';

/**
 * Editor header dispatcher. Reads only `toolbarLayout` (a single hook — no conditional hooks)
 * and renders one of the two concrete layouts, forwarding all props verbatim:
 *   - `compact`  → {@link CompactEditorHeader} (default, grouped three-zone toolbar)
 *   - `classic`  → {@link ClassicEditorHeader} (pre-redesign inline-button row)
 * The layout is toggled via the {@link ToolbarLayoutToggle} present in both and persisted in
 * `designStore`. Hidden in fullscreen (F11) by the parent.
 */
export function EditorHeader(props: Readonly<EditorHeaderProps>) {
  const toolbarLayout = useDesignStore((s) => s.toolbarLayout);
  return toolbarLayout === 'classic'
    ? <ClassicEditorHeader {...props} />
    : <CompactEditorHeader {...props} />;
}
