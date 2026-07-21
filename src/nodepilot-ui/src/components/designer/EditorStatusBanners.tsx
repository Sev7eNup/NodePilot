import { Chemistry, Close, Edit, Locked, View } from '@carbon/icons-react';
import { useEffect, useState, type ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';
import type { Workflow, StepExecution } from '../../types/api';
import { confirmDialog } from '../../stores/confirmStore';
import { useWorkflowCallRefs, WorkflowCallsInline } from './WorkflowBreadcrumbs';

interface EditorStatusBannersProps {
  // Replay banner
  replayExecutionId: string | null;
  replaySteps: StepExecution[] | undefined;
  clearReplay: () => void;
  // Test-run banner (canvas execution highlight)
  designerCanvasRunIsTerminal: boolean;
  designerCanvasRunShortId: string;
  clearDesignerCanvasHighlight: () => void;
  // Lock / role banner
  fullscreen: boolean;
  roleCanWrite: boolean;
  isLockedByMe: boolean;
  isLockedByOther: boolean;
  isAdmin: boolean;
  workflow: Workflow | undefined;
  onForceUnlock: () => void;
  isForceUnlocking: boolean;
  /** Current workflow nodes — the outgoing "Calls →" references share this strip. */
  nodes: Node[];
}

/**
 * Compact tonal status pill — replaces the old full-width colored banner bars.
 * All pills share one horizontal strip under the header; several can coexist
 * (e.g. replay + lock state) without stacking full-height rows.
 */
function StatusPill({ tone, children }: Readonly<{
  tone: 'info' | 'success' | 'warning' | 'neutral' | 'primary';
  children: ReactNode;
}>) {
  const toneCls =
    tone === 'success' ? 'bg-success-container/70 text-on-success-container border-success/30' :
    tone === 'warning' ? 'bg-warning-container/70 text-on-warning-container border-warning/30' :
    tone === 'info'    ? 'bg-info-container/70 text-on-info-container border-info/30' :
    tone === 'primary' ? 'bg-primary-fixed/40 text-primary border-primary/30' :
    'bg-surface-high text-on-surface-variant border-outline-variant/30';
  return (
    <div className={`inline-flex items-center gap-2 rounded-full border px-3 py-1 text-xs font-label max-w-full min-w-0 ${toneCls}`}>
      {children}
    </div>
  );
}

export function EditorStatusBanners({
  replayExecutionId,
  replaySteps,
  clearReplay,
  designerCanvasRunIsTerminal,
  designerCanvasRunShortId,
  clearDesignerCanvasHighlight,
  fullscreen,
  roleCanWrite,
  isLockedByMe,
  isLockedByOther,
  isAdmin,
  workflow,
  onForceUnlock,
  isForceUnlocking,
  nodes,
}: Readonly<EditorStatusBannersProps>) {
  const { t } = useTranslation(['editor', 'common']);
  // The banner shows minute-level granularity → a 60s tick is enough. State instead of
  // Date.now() during render (purity rule: no impure calls during render).
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const handle = globalThis.setInterval(() => setNowMs(Date.now()), 60_000);
    return () => globalThis.clearInterval(handle);
  }, []);

  // Outgoing "Calls →" references now share this strip instead of stacking their own row.
  const callRefs = useWorkflowCallRefs(nodes);

  const showReplay = !!replayExecutionId;
  const showTestRun = designerCanvasRunIsTerminal && !replayExecutionId;
  const showLockState = !fullscreen;
  const hasBanners = showReplay || showTestRun || showLockState;
  const showCalls = !fullscreen && callRefs.length > 0;
  if (!hasBanners && !showCalls) return null;

  return (
    <div className="wd-strip flex items-center gap-2 px-6 py-1.5 shrink-0 z-10 bg-surface border-b border-outline-variant/10">
      {hasBanners && (
      <div className="flex min-w-0 items-center flex-wrap gap-2">
      {showReplay && (
        <StatusPill tone="primary">
          <View size={13} className="shrink-0" />
          <span className="truncate">
            <strong>{t('editor:banners.replayMode')}</strong> —{' '}
            <Trans
              t={t}
              i18nKey="editor:banners.replayDescription"
              values={{ shortId: replayExecutionId!.slice(0, 8) }}
              components={{ mono: <span className="font-mono" /> }}
            />
            {!replaySteps && t('editor:banners.replayLoadingSteps')}
          </span>
          <button
            onClick={clearReplay}
            className="shrink-0 p-0.5 rounded-full hover:bg-primary/20 transition-colors"
            title={t('editor:banners.replayClose')}
          >
            <Close size={13} />
          </button>
        </StatusPill>
      )}

      {showTestRun && (
        <StatusPill tone="success">
          <Chemistry size={13} className="shrink-0" />
          <span className="truncate">
            <strong>{t('editor:banners.testRunMode')}</strong> —{' '}
            <Trans
              t={t}
              i18nKey="editor:banners.testRunDescription"
              values={{ shortId: designerCanvasRunShortId }}
              components={{ mono: <span className="font-mono" /> }}
            />
          </span>
          <button
            onClick={clearDesignerCanvasHighlight}
            className="shrink-0 p-0.5 rounded-full hover:bg-success/20 transition-colors"
            title={t('editor:banners.testRunHide')}
            aria-label={t('editor:banners.testRunHide')}
          >
            <Close size={13} />
          </button>
        </StatusPill>
      )}

      {showLockState && (() => {
        if (!roleCanWrite) {
          return (
            <StatusPill tone="neutral">
              <View size={12} className="shrink-0" />
              <span className="truncate"><strong>{t('editor:banners.viewerReadOnly')}</strong> {t('editor:banners.viewerReadOnlyDetail')}</span>
            </StatusPill>
          );
        }
        if (isLockedByOther) {
          const lockedAt = workflow?.checkedOutAt ? new Date(workflow.checkedOutAt) : null;
          const minsAgo = lockedAt ? Math.floor((nowMs - lockedAt.getTime()) / 60000) : null;
          const userName = workflow?.checkedOutByUserName ?? t('common:unknown');
          return (
            <StatusPill tone="warning">
              <Locked size={12} className="shrink-0" />
              <span className="truncate">
                <strong>{t('editor:banners.lockedByOtherTitle')}</strong>{' '}
                <Trans
                  t={t}
                  i18nKey="editor:banners.lockedByOtherWho"
                  values={{ user: userName }}
                  components={{ mono: <span className="font-mono" /> }}
                />
                {minsAgo !== null && (
                  <Trans
                    t={t}
                    i18nKey="editor:banners.lockedByOtherSince"
                    values={{ minutes: minsAgo }}
                    components={{ strong: <strong /> }}
                  />
                )}
                {t('editor:banners.lockedByOtherTrailing')}
              </span>
              {isAdmin && (
                <button
                  onClick={async () => {
                    if (await confirmDialog(t('editor:banners.forceUnlockConfirm', { user: userName }))) {
                      onForceUnlock();
                    }
                  }}
                  disabled={isForceUnlocking}
                  className="shrink-0 px-2 py-0.5 rounded-full bg-warning-container hover:brightness-95 text-on-warning-container border border-warning/40 text-[11px] font-semibold disabled:opacity-50 transition-all"
                  title={t('editor:banners.lockedByOtherForceUnlockTitle')}
                >
                  {t('editor:banners.lockedByOtherForceUnlock')}
                </button>
              )}
            </StatusPill>
          );
        }
        if (isLockedByMe) {
          return (
            <StatusPill tone="primary">
              <Edit size={12} className="shrink-0" />
              <span className="truncate">
                <strong>{t('editor:banners.lockedByMeTitle')}</strong> — {t('editor:banners.lockedByMeDetail')}
              </span>
            </StatusPill>
          );
        }
        // Not locked, role allows write — read-only by default until "Edit" is clicked.
        return (
          <StatusPill tone={workflow?.isEnabled ? 'warning' : 'neutral'}>
            <View size={12} className="shrink-0" />
            <span className="truncate">
              {workflow?.isEnabled ? (
                <>
                  <strong>{t('editor:banners.productiveTitle')}</strong> —{' '}
                  <Trans
                    t={t}
                    i18nKey="editor:banners.productiveDetail"
                    components={{ strong: <strong /> }}
                  />
                </>
              ) : (
                <>
                  <strong>{t('editor:banners.disabledTitle')}</strong> —{' '}
                  <Trans
                    t={t}
                    i18nKey="editor:banners.disabledDetail"
                    components={{ strong: <strong /> }}
                  />
                </>
              )}
            </span>
          </StatusPill>
        );
      })()}
      </div>
      )}
      {showCalls && (
        <div className={`flex min-w-0 items-center ${hasBanners ? 'ml-auto border-l border-outline-variant/40 pl-3' : ''}`}>
          <WorkflowCallsInline refs={callRefs} />
        </div>
      )}
    </div>
  );
}
