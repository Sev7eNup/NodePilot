import { ArrowsHorizontal, ConnectTarget, FlowConnection, TrashCan, View, ViewOff } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { ContextMenuShell, ContextMenuItem, makeMenuAction } from '../../common/ContextMenuShell';
import { useDesignStore } from '../../../stores/designStore';

interface Props {
  x: number;
  y: number;
  isDisabled: boolean;
  hasCustomShape: boolean;
  onToggleDisabled: () => void;
  onDetachTarget: () => void;
  onSwapSourceTarget: () => void;
  onResetShape: () => void;
  onDelete: () => void;
  onClose: () => void;
}

/**
 * Right-click menu for a canvas edge. Shares NodeContextMenu's interaction model
 * (outside-click/Escape close, action-then-close) via the shared ContextMenuShell.
 *
 * "Edit condition" is not a menu item: right-click already selects the edge, which opens
 * the EdgePropertiesPanel where the condition is edited. "Detach target" stays available
 * outside expert mode because re-routing an edge is a primary editing action, and the
 * drag-based alternative (React Flow's `edgesReconnectable`) needs a precise grab on the
 * endpoint, which is hard on large graphs where source and target are far apart.
 */
export function EdgeContextMenu({
  x, y, isDisabled, hasCustomShape,
  onToggleDisabled, onDetachTarget, onSwapSourceTarget, onResetShape, onDelete, onClose,
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
      <ContextMenuItem
        icon={<ConnectTarget size={14} />}
        label={t('editor:edgeMenu.detachTarget')}
        onClick={action(onDetachTarget)}
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
