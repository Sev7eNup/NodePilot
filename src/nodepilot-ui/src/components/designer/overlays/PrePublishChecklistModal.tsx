import {
  CheckmarkFilled,
  Close,
  Information,
  Rocket,
  SecurityServices,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useMemo } from 'react';
import { type Node } from '@xyflow/react';
import { useTranslation } from 'react-i18next';
import type { LintIssue, LintResult } from '../../../lib/workflowLint';

interface Props {
  result: LintResult;
  nodes: Node[];
  workflowName: string;
  isPending: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  /** Jumps to the affected node in the designer (selects it and centers the view). */
  onJumpToNode: (nodeId: string) => void;
  /** Jumps to the affected edge in the designer (selects it and centers the view). */
  onJumpToEdge?: (edgeId: string) => void;
}

/**
 * Modal that fires before publish. Routes:
 *   - Errors present  → the "Publish anyway" button is greyed out; only "Close" is available.
 *   - Warnings only   → both buttons are clickable; the confirm label emphasizes "anyway".
 *   - Empty (clean)   → not rendered at all (caller short-circuits straight to publish).
 *
 * Why a modal at all: the user clicks Publish *expecting* the workflow to go live, so
 * silent lint warnings shouldn't block them — but unconnected triggers and missing
 * machines have caused production incidents before, and the per-keystroke lint pill in
 * the toolbar is easy to ignore. The pre-publish gate creates one explicit "are you
 * sure" moment without adding friction to the always-on lint flow.
 */
export function PrePublishChecklistModal({
  result, nodes, workflowName, isPending, onConfirm, onCancel, onJumpToNode, onJumpToEdge,
}: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const errorCount = result.errors.length;
  const warnCount = result.warnings.length;
  const hasErrors = errorCount > 0;
  const all = useMemo(() => [...result.errors, ...result.warnings], [result]);

  const primaryButtonLabel = hasErrors
    ? t('prePublish.primaryBlocked')
    : warnCount > 0
      ? t('prePublish.primaryWithWarnings')
      : t('prePublish.primaryClean');

  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      onClick={onCancel}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
      role="dialog"
      aria-modal="true"
      aria-labelledby="pre-publish-title"
      tabIndex={-1}
    >
      <div
        className="w-[640px] max-w-[95vw] max-h-[85vh] bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30 overflow-hidden flex flex-col"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        {/* Header */}
        <div className="px-5 py-3 border-b border-outline-variant/20 flex items-center justify-between bg-surface-high">
          <div className="flex items-center gap-2 min-w-0">
            {hasErrors ? (
              <SecurityServices size={16} className="text-error shrink-0" />
            ) : warnCount > 0 ? (
              <WarningAltFilled size={16} className="text-amber-700 shrink-0" />
            ) : (
              <CheckmarkFilled size={16} className="text-green-700 shrink-0" />
            )}
            <h2
              id="pre-publish-title"
              className="font-headline text-sm font-bold text-on-surface truncate"
            >
              {t('prePublish.title')}
              {workflowName && (
                <span className="ml-2 font-label font-normal text-on-surface-variant">
                  · {workflowName}
                </span>
              )}
            </h2>
          </div>
          <button
            onClick={onCancel}
            className="text-on-surface-variant hover:text-on-surface shrink-0"
            aria-label={t('prePublish.close')}
          >
            <Close size={14} />
          </button>
        </div>

        {/* Summary strip */}
        <div className="px-5 py-3 grid grid-cols-2 gap-3 border-b border-outline-variant/10">
          <div className={`rounded-md px-3 py-2 ${hasErrors ? 'bg-error-container text-on-error-container' : 'bg-surface-high text-on-surface-variant'}`}>
            <div className="text-[10px] font-label font-bold uppercase tracking-widest opacity-80">{t('prePublish.errorsLabel')}</div>
            <div className="font-headline text-lg font-bold tabular-nums">{errorCount}</div>
          </div>
          <div className={`rounded-md px-3 py-2 ${warnCount > 0 ? 'bg-amber-100 text-amber-900' : 'bg-surface-high text-on-surface-variant'}`}>
            <div className="text-[10px] font-label font-bold uppercase tracking-widest opacity-80">{t('prePublish.warningsLabel')}</div>
            <div className="font-headline text-lg font-bold tabular-nums">{warnCount}</div>
          </div>
        </div>

        {/* Issue list */}
        <div className="flex-1 overflow-y-auto" data-testid="pre-publish-issue-list">
          {all.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 px-6 text-center">
              <CheckmarkFilled size={48} className="text-green-600 mb-3" />
              <div className="font-headline text-base font-bold text-on-surface mb-1">
                {t('prePublish.clean')}
              </div>
              <div className="font-label text-xs text-on-surface-variant">
                {t('prePublish.cleanDetail')}
              </div>
            </div>
          ) : (
            <ul className="divide-y divide-outline-variant/10">
              {all.map((issue, i) => (
                <IssueRow
                  key={`${issue.code}-${issue.nodeId ?? issue.edgeId ?? i}-${i}`}
                  issue={issue}
                  nodes={nodes}
                  onJumpToNode={onJumpToNode}
                  onJumpToEdge={onJumpToEdge}
                />
              ))}
            </ul>
          )}
        </div>

        {/* Footer */}
        <div className="px-5 py-3 border-t border-outline-variant/20 flex items-center justify-between bg-surface-high">
          <div className="flex items-center gap-1.5 text-[11px] text-on-surface-variant">
            <Information size={12} />
            {hasErrors
              ? t('prePublish.blockedDetail')
              : warnCount > 0
                ? t('prePublish.warnDetail')
                : t('prePublish.noIssuesDetail')}
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={onCancel}
              className="rounded-md h-9 px-3 bg-surface-low hover:bg-surface text-on-surface font-label text-xs font-semibold transition-colors"
            >
              {t('prePublish.cancel')}
            </button>
            <button
              onClick={onConfirm}
              disabled={hasErrors || isPending}
              className="flex items-center gap-1.5 rounded-md h-9 px-3 bg-gradient-to-br from-primary to-primary-container text-on-primary font-label text-xs font-semibold shadow-sm hover:shadow-md transition-all disabled:opacity-50 disabled:cursor-not-allowed"
              data-testid="pre-publish-confirm"
            >
              <Rocket size={13} />
              {isPending ? t('prePublish.publishing') : primaryButtonLabel}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function IssueRow({
  issue, nodes, onJumpToNode, onJumpToEdge,
}: Readonly<{ issue: LintIssue; nodes: Node[]; onJumpToNode: (id: string) => void; onJumpToEdge?: (id: string) => void }>) {
  const node = issue.nodeId ? nodes.find((n) => n.id === issue.nodeId) : undefined;
  const nodeLabel = node ? ((node.data as Record<string, unknown>)?.label as string) || issue.nodeId : undefined;
  const clickable = !!issue.nodeId || !!issue.edgeId;
  return (
    <li>
      <button
        onClick={() => {
          if (issue.nodeId) onJumpToNode(issue.nodeId);
          else if (issue.edgeId) onJumpToEdge?.(issue.edgeId);
        }}
        disabled={!clickable}
        className="w-full text-left px-5 py-2.5 hover:bg-surface-high transition-colors disabled:cursor-default disabled:hover:bg-transparent"
      >
        <div className="flex items-center gap-1.5 mb-0.5">
          <span
            className={`inline-block w-1.5 h-1.5 rounded-full ${
              issue.severity === 'error' ? 'bg-error' : 'bg-amber-500'
            }`}
          />
          <span className="font-label text-[10px] font-bold uppercase tracking-widest text-outline">
            {issue.code}
          </span>
          {nodeLabel && (
            <span className="font-mono text-[10px] text-on-surface-variant truncate">
              → {nodeLabel}
            </span>
          )}
          {issue.edgeId && !nodeLabel && (
            <span className="font-mono text-[10px] text-on-surface-variant truncate">
              → edge {issue.edgeId}
            </span>
          )}
        </div>
        <p className="font-label text-xs text-on-surface leading-snug">{issue.message}</p>
      </button>
    </li>
  );
}
