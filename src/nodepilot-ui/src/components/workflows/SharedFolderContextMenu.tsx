import { Edit, TrashCan, UserRole } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { ContextMenuShell, ContextMenuItem, makeMenuAction } from '../common/ContextMenuShell';

interface Props {
  x: number;
  y: number;
  /** Omit to hide the entry — the caller lacks `capabilities.canAdmin` on this folder. */
  onManagePermissions?: () => void;
  /** Omit to hide the entry — no `capabilities.canEdit`, or the folder is Root. */
  onRename?: () => void;
  /** Omit to hide the entry — no `capabilities.canEdit`, or the folder is Root. */
  onDelete?: () => void;
  onClose: () => void;
}

/**
 * Right-click pop-up on a folder row in the SharedFolderTree sidebar. Uses the shared
 * ContextMenuShell for outside-click + Escape close behaviour, so the affordance feels
 * familiar to users who already use the designer's edge/node menus.
 *
 * Every item is optional and the parent (SharedFolderTree) decides per folder which
 * handlers to pass — Root gets permissions only, a read-only folder gets no menu at all.
 * The parent also gates whether to OPEN the menu in the first place.
 */
export function SharedFolderContextMenu({
  x,
  y,
  onManagePermissions,
  onRename,
  onDelete,
  onClose,
}: Readonly<Props>) {
  const { t } = useTranslation(['workflows', 'common']);
  const action = makeMenuAction(onClose);
  // Only draw a divider when both groups actually rendered, so a permissions-only menu
  // (Root) doesn't end in a dangling rule.
  const showDivider = !!onManagePermissions && (!!onRename || !!onDelete);

  return (
    <ContextMenuShell x={x} y={y} onClose={onClose} positioning="fixed" zIndex="z-50" testId="shared-folder-context-menu">
      {onManagePermissions && (
        <ContextMenuItem
          icon={<UserRole size={14} />}
          label={t('workflows:managePermissions')}
          onClick={action(onManagePermissions)}
          testId="shared-folder-menu-permissions"
        />
      )}
      {showDivider && <div className="my-1 border-t border-outline-variant/20" />}
      {onRename && (
        <ContextMenuItem
          icon={<Edit size={14} />}
          label={t('workflows:folder.rename', { defaultValue: 'Umbenennen' })}
          onClick={action(onRename)}
          testId="shared-folder-menu-rename"
        />
      )}
      {onRename && onDelete && <div className="my-1 border-t border-outline-variant/20" />}
      {onDelete && (
        <ContextMenuItem
          icon={<TrashCan size={14} />}
          label={t('workflows:folder.delete', { defaultValue: 'Löschen' })}
          onClick={action(onDelete)}
          danger
          testId="shared-folder-menu-delete"
        />
      )}
    </ContextMenuShell>
  );
}
