import { ArrowsHorizontal, FlowConnection, TrashCan, View, ViewOff } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { ContextMenuShell, ContextMenuItem, makeMenuAction } from '../../common/ContextMenuShell';
import { useDesignStore } from '../../../stores/designStore';

interface Props {
  x: number;
  y: number;
  isDisabled: boolean;
  hasCustomShape: boolean;
  onToggleDisabled: () => void;
  onSwapSourceTarget: () => void;
  onResetShape: () => void;
  onDelete: () => void;
  onClose: () => void;
}

/**
 * Right-click pop-up on a canvas edge. Mirrors NodeContextMenu's interaction model
 * (outside-click + Escape close, action+close-on-click) via the shared ContextMenuShell.
 *
 * "Edit condition" is intentionally NOT a menu item — the right-click handler in the parent
 * already auto-selects the edge, which surfaces the EdgePropertiesPanel where the condition
 * is edited. A menu item that does nothing extra would be redundant.
 */
export function EdgeContextMenu({
  x, y, isDisabled, hasCustomShape,
  onToggleDisabled, onSwapSourceTarget, onResetShape, onDelete, onClose,
}: Readonly<Props>) {
  const { t } = useTranslation(['editor', 'common']);
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  const action = makeMenuAction(onClose);

  return (
    <ContextMenuShell x={x} y={y} onClose={onClose} positioning="absolute" zIndex="z-30">
      <ContextMenuItem
        icon={isDisabled ? <View size={14} /> : <ViewOff size={14} />}
        label={isDisabled ? t('editor:edgeMenu.enableEdge', { defaultValue: 'Enable edge' }) : t('editor:edgeMenu.toggleDisabled')}
        onClick={action(onToggleDisabled)}
      />
      {expertMode && <ContextMenuItem
        icon={<ArrowsHorizontal size={14} />}
        label={t('editor:edgeMenu.swap', { defaultValue: 'Swap source ↔ target' })}
        onClick={action(onSwapSourceTarget)}
      />}
      {expertMode && hasCustomShape && (
        <ContextMenuItem
          icon={<FlowConnection size={14} />}
          label={t('editor:edgeMenu.resetShape')}
          onClick={action(onResetShape)}
        />
      )}
      <div className="my-1 border-t border-outline-variant/20" />
      <ContextMenuItem icon={<TrashCan size={14} />} label={t('common:delete')} onClick={action(onDelete)} danger />
    </ContextMenuShell>
  );
}
