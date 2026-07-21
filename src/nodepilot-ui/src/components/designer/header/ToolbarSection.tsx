import { useContext, useEffect, useRef, type CSSProperties, type ReactNode } from 'react';
import { ToolbarGlowContext } from './toolbarGlowContext';
import { SECTION_GLOW_COLOR, type SectionId } from './sectionColors';

/**
 * Wraps one toolbar cluster. Keeps the original `flex items-center gap-1` layout but adds:
 *   • registration with <ToolbarGlow> so the provider can write a proximity-driven
 *     `--np-glow` (0..1) onto this element on pointer move;
 *   • a `.np-glow-bloom` child that renders the diffuse colored halo at the section's bottom
 *     edge — opacity = `--np-glow * --np-glow-strength` (see index.css).
 *
 * The bloom is purely decorative: aria-hidden, pointer-events-none, and stacked behind the
 * opaque buttons via the `.np-toolbar-section` isolation rule in index.css.
 */
export function ToolbarSection({
  id,
  children,
  className,
}: Readonly<{ id: SectionId; children: ReactNode; className?: string }>) {
  const { register } = useContext(ToolbarGlowContext);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    return register(el);
  }, [register]);

  return (
    <div
      ref={ref}
      className={`np-toolbar-section flex items-center gap-0.5 rounded-lg bg-surface-container/75 p-0.5${className ? ` ${className}` : ''}`}
      style={{ '--np-section-color': SECTION_GLOW_COLOR[id] } as CSSProperties}
    >
      {children}
      <span aria-hidden className="np-glow-bloom" />
    </div>
  );
}
