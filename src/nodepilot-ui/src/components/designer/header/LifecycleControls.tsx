import { Edit, Locked, Power, Rocket, Save } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { confirmDialog } from '../../../stores/confirmStore';
import type { EditorHeaderProps } from './editorHeaderTypes';

type LifecycleControlsProps = Pick<EditorHeaderProps,
  'workflow' | 'canWrite' | 'isDirty' | 'isLockedByMe' | 'isLockedByOther'
  | 'onLock' | 'isLocking' | 'onUnlock' | 'isUnlocking' | 'onSave'
  | 'isPublishing' | 'onRequestPublish' | 'onDisable' | 'isDisabling' | 'isEnabling'
>;

/**
 * Edit-lock trio + Save + the Publish/Disable four-state toggle — the single source of the
 * lifecycle matrix, shared by both header layouts so it can't drift. Rendered inside a
 * `roleCanWrite` gate by the caller.
 */
export function LifecycleControls({
  workflow, canWrite, isDirty, isLockedByMe, isLockedByOther,
  onLock, isLocking, onUnlock, isUnlocking, onSave, isPublishing, onRequestPublish, onDisable, isDisabling, isEnabling,
}: Readonly<LifecycleControlsProps>) {
  const { t } = useTranslation(['editor', 'common']);

  return (
    <>
      {/* Edit-lock entry: Start editing / Stop editing / locked-by-other indicator. */}
      {!isLockedByMe && !isLockedByOther && (
        <button
          type="button"
          onClick={onLock}
          disabled={isLocking}
          className="flex items-center justify-center rounded-md h-9 w-9 bg-primary/15 hover:bg-primary/25 text-primary border border-primary/30 transition-colors disabled:opacity-50"
          title={workflow?.isEnabled ? t('editor:lockOpenWhileEnabled') : t('editor:lockOpenWhileDisabled')}
          aria-label={t('editor:lock')}
        >
          <Edit size={15} />
        </button>
      )}
      {isLockedByMe && (
        <button
          type="button"
          onClick={async () => {
            if (isDirty && !(await confirmDialog(t('editor:endEditingDirtyConfirm')))) return;
            onUnlock();
          }}
          disabled={isUnlocking}
          className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors disabled:opacity-50"
          title={t('editor:endEditingTitleFull')}
          aria-label={t('editor:endEditing')}
        >
          <Locked size={15} />
        </button>
      )}
      {isLockedByOther && (
        <span
          className="flex items-center justify-center rounded-md h-9 w-9 bg-warning-container text-on-warning-container border border-warning/30 opacity-90"
          title={t('editor:lockedByOtherTitle', { user: workflow?.checkedOutByUserName ?? t('common:unknown') })}
        >
          <Locked size={15} />
        </span>
      )}
      {/* Save — only canWrite (= lock-by-me); an intermediate save only makes sense while editing. */}
      {canWrite && (
        <button
          type="button"
          onClick={onSave}
          className="relative flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface transition-colors"
          title={isDirty ? t('editor:saveDirty') : t('editor:saveClean')}
        >
          <Save size={18} />
          {isDirty && <span className="absolute top-1 right-1 w-2 h-2 rounded-full bg-warning ring-1 ring-surface-high" />}
        </button>
      )}
      {/* Publish/Disable toggle — four states, one button slot:
            - Productive (isEnabled)         → "Disable" (red)      → /disable
            - Disabled + lock-by-me          → "Publish" (primary)  → /publish (atomic)
            - Disabled + unlocked            → "Publish" (primary)  → /enable (live only)
            - Disabled + lock-by-other       → "Publish" disabled, tooltip explains why */}
      {(() => {
        if (workflow?.isEnabled) {
          return (
            <button
              type="button"
              onClick={async () => {
                if (await confirmDialog(t('editor:stopWorkflowConfirm'))) onDisable();
              }}
              disabled={isDisabling}
              className="flex items-center justify-center rounded-md h-9 w-9 bg-error text-on-error hover:bg-error/90 shadow-sm transition-all disabled:opacity-60 disabled:cursor-not-allowed"
              title={t('editor:disableTitle')}
              aria-label={t('editor:disable')}
            >
              <Power size={16} />
            </button>
          );
        }
        const lockedByOther = isLockedByOther;
        const pending = isPublishing || isEnabling;
        const title = lockedByOther
          ? t('editor:publishLocked', { user: workflow?.checkedOutByUserName ?? t('common:unknown') })
          : isLockedByMe
            ? t('editor:publishAtomicTitle')
            : t('editor:publishEnableTitle');
        return (
          <button
            type="button"
            onClick={onRequestPublish}
            disabled={lockedByOther || pending}
            className="flex items-center justify-center rounded-md h-9 w-9 bg-gradient-to-br from-primary to-primary-container text-on-primary shadow-sm hover:shadow-md transition-all disabled:opacity-50 disabled:cursor-not-allowed"
            title={title}
            aria-label={t('editor:publish')}
          >
            <Rocket size={16} />
          </button>
        );
      })()}
    </>
  );
}
