import { useTranslation } from 'react-i18next';
import { toBlob } from 'html-to-image';
import { downloadFromApi } from '../api/client';
import { toast } from '../stores/toastStore';
import {
  AuthBoundaryChangedError,
  assertAuthBoundaryGenerationCurrent,
  captureAuthBoundaryGeneration,
} from '../security/authBoundary';

/** Exports the open workflow as JSON (server export) or as a PNG of the canvas. */
export function useWorkflowExport({ workflowId, name, canvasRef }: {
  workflowId: string | undefined;
  name: string;
  canvasRef: React.RefObject<HTMLElement | null>;
}) {
  const { t } = useTranslation(['common']);

  // Captures the whole `.react-flow` element at its current size and viewport transform, so
  // the PNG matches what the user sees (background pattern, edges, nodes, panels). Editor
  // chrome (Controls, MiniMap, attribution) is filtered out; it is UI, not workflow content.
  const exportPng = async () => {
    const flow = canvasRef.current?.querySelector('.react-flow') as HTMLElement | null;
    if (!flow) return;
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    const surfaceBg = canvasRef.current
      ? getComputedStyle(canvasRef.current).backgroundColor
      : '#ffffff';
    try {
      const blob = await toBlob(flow, {
        backgroundColor: surfaceBg,
        pixelRatio: Math.max(globalThis.devicePixelRatio || 1, 2),
        cacheBust: true,
        filter: (node) => {
          if (!(node instanceof HTMLElement)) return true;
          const cls = node.classList;
          if (!cls) return true;
          if (cls.contains('react-flow__controls')) return false;
          if (cls.contains('react-flow__minimap')) return false;
          if (cls.contains('react-flow__attribution')) return false;
          return true;
        },
      });
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      if (!blob) return;
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${name || 'workflow'}.png`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      // A changed auth context discards the image on purpose; that is not an export failure.
      if (err instanceof AuthBoundaryChangedError) return;
      toast.error(t('common:exportFailed', { message: (err as Error).message }));
    }
  };

  const exportJson = async () => {
    if (!workflowId) return;
    try {
      await downloadFromApi(`/workflows/${workflowId}/export`, `${name || 'workflow'}.workflow.json`);
    } catch (err) {
      toast.error(t('common:exportFailed', { message: (err as Error).message }));
    }
  };

  return {
    exportPng: () => { void exportPng(); },
    exportJson: () => { void exportJson(); },
  };
}
