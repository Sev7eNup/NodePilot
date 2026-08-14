import { CircleStroke, Copy, TrashCan, View, ViewOff } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { ContextMenuShell, ContextMenuItem, makeMenuAction } from '../../common/ContextMenuShell';
import { useDesignStore } from '../../../stores/designStore';

interface Props {
  x: number;
  y: number;
  isDisabled: boolean;
  hasBreakpoint: boolean;
  onDuplicate: () => void;
  onToggleDisabled: () => void;
  onToggleBreakpoint: () => void;
  onDelete: () => void;
  onClose: () => void;
}

export function NodeContextMenu({ x, y, isDisabled, hasBreakpoint, onDuplicate, onToggleDisabled, onToggleBreakpoint, onDelete, onClose }: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  const action = makeMenuAction(onClose);

  return (
    <ContextMenuShell x={x} y={y} onClose={onClose} positioning="absolute" zIndex="z-30" minWidth="min-w-[160px]">
      <ContextMenuItem icon={<Copy size={14} />} label={t('nodeMenu.duplicate')} onClick={action(onDuplicate)} />
      <ContextMenuItem
        icon={isDisabled ? <View size={14} /> : <ViewOff size={14} />}
        label={isDisabled ? t('nodeMenu.enableStep') : t('nodeMenu.disableStep')}
        onClick={action(onToggleDisabled)}
      />
      {expertMode && <ContextMenuItem
        icon={<CircleStroke size={14} className={hasBreakpoint ? 'text-red-500 fill-red-500' : ''} />}
        label={hasBreakpoint ? t('nodeMenu.removeBreakpoint') : t('nodeMenu.addBreakpoint')}
        onClick={action(onToggleBreakpoint)}
      />}
      <div className="my-1 border-t border-outline-variant/20" />
      <ContextMenuItem icon={<TrashCan size={14} />} label={t('nodeMenu.delete')} onClick={action(onDelete)} danger />
    </ContextMenuShell>
  );
}
