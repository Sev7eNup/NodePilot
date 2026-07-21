import { BrandLogo } from '../BrandLogo';

// Thin wrapper kept for the designer header's call site. The actual mark is the
// skin-aware BrandLogo, so the editor logo recolors with the active skin just like
// the "Workflow / Designer" wordmark next to it.
export function WorkflowDesignerIcon({ className }: Readonly<{ className?: string }>) {
  return <BrandLogo alt="NodePilot" className={className} />;
}
