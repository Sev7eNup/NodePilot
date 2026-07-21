import { PaintBrush } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useDesignStore } from '../../../stores/designStore';

/**
 * Switches the designer between the Atelier design language and the classic look.
 * Deliberately a `role="switch"` BUTTON, not a checkbox input — a global checkbox in the
 * editor header would get caught by e2e specs using `getByRole('checkbox').first()`.
 * Rendered trailing in both header layouts; persisted in `designStore.designerTheme`.
 */
export function AtelierThemeToggle() {
  const { t } = useTranslation('editor');
  const designerTheme = useDesignStore((s) => s.designerTheme);
  const toggleDesignerTheme = useDesignStore((s) => s.toggleDesignerTheme);
  const isAtelier = designerTheme === 'atelier';

  return (
    <button
      type="button"
      role="switch"
      aria-checked={isAtelier}
      onClick={toggleDesignerTheme}
      data-testid="toggle-atelier-theme"
      className={`flex items-center justify-center rounded-md h-9 w-9 shadow-sm transition-colors ${
        isAtelier ? 'bg-primary/15 text-primary' : 'bg-surface-high hover:bg-surface-highest text-on-surface-variant'
      }`}
      title={isAtelier ? t('atelierTheme.switchToClassic') : t('atelierTheme.switchToAtelier')}
    >
      <PaintBrush size={16} />
    </button>
  );
}
