import { Close, WarningAltFilled } from '@carbon/icons-react';
import { useEffect, useMemo, useRef } from 'react';
import { type Node, type Edge } from '@xyflow/react';
import { useTranslation } from 'react-i18next';
import { type LintIssue } from '../../../lib/workflowLint';

export function LintPanel({ result, nodes, edges, onJump, onJumpEdge, onClose }: Readonly<{
  result: { errors: LintIssue[]; warnings: LintIssue[] };
  nodes: Node[];
  edges: Edge[];
  onJump: (nodeId: string) => void;
  onJumpEdge?: (edgeId: string) => void;
  onClose: () => void;
}>) {
  const { t } = useTranslation('editor');
  void edges;
  const panelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as HTMLElement)) {
        onClose();
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [onClose]);

  const all = useMemo(
    () => [...result.errors, ...result.warnings],
    [result.errors, result.warnings],
  );
  return (
    <div ref={panelRef} className="absolute top-4 right-4 z-[46] w-[360px] max-h-[60vh] bg-surface-lowest rounded-lg shadow-xl border border-outline-variant/30 overflow-hidden flex flex-col">
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-outline-variant/20 bg-surface-high">
        <div className="flex items-center gap-2">
          <WarningAltFilled size={14} className={result.errors.length > 0 ? 'text-error' : 'text-amber-700'} />
          <h3 className="font-headline text-sm font-bold text-on-surface">{t('lintPanel.title')}</h3>
          <span className="font-label text-[10px] font-semibold text-on-surface-variant tabular-nums">
            {t('lintPanel.counts', { errors: result.errors.length, warnings: result.warnings.length })}
          </span>
        </div>
        <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface" aria-label={t('lintPanel.close')}>
          <Close size={14} />
        </button>
      </div>
      <div className="flex-1 overflow-y-auto py-1">
        {all.map((issue, i) => {
          const node = issue.nodeId ? nodes.find((n) => n.id === issue.nodeId) : undefined;
          const nodeLabel = node ? ((node.data as Record<string, unknown>)?.label as string) || issue.nodeId : undefined;
          return (
            <button
              key={`${issue.code}-${issue.nodeId ?? issue.edgeId ?? i}-${i}`}
              onClick={() => {
                if (issue.nodeId) onJump(issue.nodeId);
                else if (issue.edgeId) onJumpEdge?.(issue.edgeId);
              }}
              disabled={!issue.nodeId && !issue.edgeId}
              className="w-full text-left px-4 py-2 hover:bg-surface-high transition-colors disabled:cursor-default disabled:hover:bg-transparent border-b border-outline-variant/10 last:border-b-0"
            >
              <div className="flex items-center gap-1.5 mb-0.5">
                <span className={`inline-block w-1.5 h-1.5 rounded-full ${issue.severity === 'error' ? 'bg-error' : 'bg-amber-500'}`} />
                <span className="font-label text-[10px] font-bold uppercase tracking-widest text-outline">{issue.code}</span>
                {nodeLabel && <span className="font-mono text-[10px] text-on-surface-variant truncate">→ {nodeLabel}</span>}
                {!nodeLabel && issue.edgeId && <span className="font-mono text-[10px] text-on-surface-variant truncate">→ edge</span>}
              </div>
              <p className="font-label text-xs text-on-surface leading-snug">{issue.message}</p>
            </button>
          );
        })}
      </div>
    </div>
  );
}
