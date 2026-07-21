import { CircleStroke, Copy, TrashCan, View, ViewOff } from '@carbon/icons-react';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
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
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleMouseDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) onClose();
    };
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [onClose]);

  const action = (fn: () => void) => () => { fn(); onClose(); };

  return (
    <div
      ref={menuRef}
      className="absolute z-30 bg-surface-lowest border border-outline-variant/30 rounded-lg shadow-2xl py-1 min-w-[160px]"
      style={{ left: x, top: y }}
    >
      <MenuItem icon={<Copy size={14} />} label={t('nodeMenu.duplicate')} onClick={action(onDuplicate)} />
      <MenuItem
        icon={isDisabled ? <View size={14} /> : <ViewOff size={14} />}
        label={isDisabled ? t('nodeMenu.enableStep') : t('nodeMenu.disableStep')}
        onClick={action(onToggleDisabled)}
      />
      {expertMode && <MenuItem
        icon={<CircleStroke size={14} className={hasBreakpoint ? 'text-red-500 fill-red-500' : ''} />}
        label={hasBreakpoint ? t('nodeMenu.removeBreakpoint') : t('nodeMenu.addBreakpoint')}
        onClick={action(onToggleBreakpoint)}
      />}
      <div className="my-1 border-t border-outline-variant/20" />
      <MenuItem icon={<TrashCan size={14} />} label={t('nodeMenu.delete')} onClick={action(onDelete)} danger />
    </div>
  );
}

function MenuItem({ icon, label, onClick, danger }: Readonly<{ icon: React.ReactNode; label: string; onClick: () => void; danger?: boolean }>) {
  return (
    <button
      className={`w-full flex items-center gap-2.5 px-3 py-1.5 text-left text-xs font-label transition-colors
        ${danger ? 'text-error hover:bg-error/10' : 'text-on-surface hover:bg-surface-high'}`}
      onClick={onClick}
    >
      <span className={danger ? 'text-error' : 'text-on-surface-variant'}>{icon}</span>
      {label}
    </button>
  );
}
