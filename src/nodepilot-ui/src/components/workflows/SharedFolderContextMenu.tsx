import { Edit, TrashCan } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { ContextMenuShell, ContextMenuItem, makeMenuAction } from '../common/ContextMenuShell';

interface Props {
  x: number;
  y: number;
  onRename: () => void;
  onDelete: () => void;
  onClose: () => void;
}

/**
 * Right-click pop-up on a folder row in the SharedFolderTree sidebar. Uses the shared
 * ContextMenuShell for outside-click + Escape close behaviour, so the affordance feels
 * familiar to users who already use the designer's edge/node menus.
 *
 * The parent (SharedFolderTree) gates whether to OPEN the menu in the first place
 * (canEdit + non-Root); this component only renders the items and assumes the gate
 * already passed.
 */
export function SharedFolderContextMenu({ x, y, onRename, onDelete, onClose }: Readonly<Props>) {
  const { t } = useTranslation(['workflows', 'common']);
  const action = makeMenuAction(onClose);

  return (
    <ContextMenuShell x={x} y={y} onClose={onClose} positioning="fixed" zIndex="z-50" testId="shared-folder-context-menu">
      <ContextMenuItem
        icon={<Edit size={14} />}
        label={t('workflows:folder.rename', { defaultValue: 'Umbenennen' })}
        onClick={action(onRename)}
        testId="shared-folder-menu-rename"
      />
      <div className="my-1 border-t border-outline-variant/20" />
      <ContextMenuItem
        icon={<TrashCan size={14} />}
        label={t('workflows:folder.delete', { defaultValue: 'Löschen' })}
        onClick={action(onDelete)}
        danger
        testId="shared-folder-menu-delete"
      />
    </ContextMenuShell>
  );
}
