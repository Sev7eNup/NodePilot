import { Checkbox, Debug, Play, WarningAltFilled } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { api } from '../../../api/client';
import { useDesignStore } from '../../../stores/designStore';
import type { EditorHeaderProps } from './editorHeaderTypes';

type RunControlsProps = Pick<EditorHeaderProps,
  'roleCanWrite' | 'liveExecution' | 'handleRunClick' | 'lintResult' | 'setLintPanelOpen'
> & {
  /** `cta` = the compact green "Run" call-to-action with a label; `icon` = the classic
   *  icon-only Play square. The accessible name stays `editor:testRun` in both. */
  variant: 'cta' | 'icon';
};

/**
 * Run / Cancel / Debug buttons plus the lint pill — the single source of run-related toolbar
 * logic, shared by both header layouts. Only the primary Run button's presentation differs via
 * `variant`; cancel-while-running, the expert-only Debug button and the lint pill are identical.
 */
export function RunControls({ variant, roleCanWrite, liveExecution, handleRunClick, lintResult, setLintPanelOpen }: Readonly<RunControlsProps>) {
  const { t } = useTranslation('editor');
  const isExpert = useDesignStore((s) => s.designerMode === 'expert');
  const lintCount = lintResult.errors.length + lintResult.warnings.length;

  return (
    <>
      {roleCanWrite && (
        liveExecution?.status === 'Running' ? (
          <button
            type="button"
            onClick={async () => {
              try { await api.post(`/executions/${liveExecution.executionId}/cancel`, {}); } catch { /* ignore */ }
            }}
            className="flex items-center justify-center gap-1.5 rounded-md h-9 px-4 bg-error text-on-error hover:bg-error/90 text-sm font-label font-semibold transition-colors"
            title={t('cancelRunning')}
          >
            <Checkbox size={12} fill="currentColor" /> {t('cancelButton')}
          </button>
        ) : (
          <>
            {variant === 'cta' ? (
              // Primary CTA: the green "Run" button (skin-stable success token). Its accessible
              // name stays "Test run" so existing tests keep resolving it.
              (<button
                type="button"
                onClick={() => handleRunClick(false)}
                className="flex items-center justify-center gap-1.5 rounded-md h-9 px-3.5 bg-success text-on-success hover:brightness-110 text-sm font-label font-semibold shadow-sm transition-all"
                title={t('testRun')}
                aria-label={t('testRun')}
              >
                <Play size={16} fill="currentColor" />
                <span>{t('runButtonLabel')}</span>
              </button>)
            ) : (
              // Classic: the pre-redesign icon-only Play square (no green CTA).
              (<button
                type="button"
                onClick={() => handleRunClick(false)}
                className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface transition-colors"
                title={t('testRun')}
                aria-label={t('testRun')}
              >
                <Play size={16} />
              </button>)
            )}
            {isExpert && (
              <button
                type="button"
                onClick={() => handleRunClick(true)}
                className="flex items-center justify-center rounded-md h-9 w-9 bg-error-container/60 hover:bg-error-container text-on-error-container border border-error/30 transition-colors"
                title={t('debugRun')}
                aria-label={t('debugRun')}
              >
                <Debug size={16} />
              </button>
            )}
          </>
        )
      )}
      {lintCount > 0 && (
        <button
          type="button"
          onClick={() => setLintPanelOpen((o) => !o)}
          className={`flex items-center gap-1.5 rounded-md h-9 px-2.5 text-xs font-label font-semibold transition-colors ${
            lintResult.errors.length > 0
              ? 'bg-error-container text-on-error-container hover:brightness-110'
              : 'bg-warning-container text-on-warning-container hover:brightness-110'
          }`}
          title={t('lintTooltip', { errors: lintResult.errors.length, warnings: lintResult.warnings.length })}
        >
          <WarningAltFilled size={15} />
          {lintCount}
        </button>
      )}
    </>
  );
}
