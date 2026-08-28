import { BrandLogo } from '../BrandLogo';

// Thin wrapper for the designer header. The mark itself is the skin-aware BrandLogo, so the
// editor logo recolors with the active skin like the wordmark next to it.
export function WorkflowDesignerIcon({ className }: Readonly<{ className?: string }>) {
  return <BrandLogo alt="NodePilot" className={className} />;
}
