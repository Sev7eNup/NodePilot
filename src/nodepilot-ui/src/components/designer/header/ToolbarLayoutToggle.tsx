import { Row } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useDesignStore } from '../../../stores/designStore';

/**
 * Toggles the editor-header between the compact grouped toolbar and the classic inline-button
 * row. Rendered in BOTH layouts (always visible, even when the classic row wraps) so users can
 * always switch back. The choice is persisted in `designStore.toolbarLayout`.
 */
export function ToolbarLayoutToggle() {
  const { t } = useTranslation('editor');
  const toolbarLayout = useDesignStore((s) => s.toolbarLayout);
  const setToolbarLayout = useDesignStore((s) => s.setToolbarLayout);
  const isClassic = toolbarLayout === 'classic';

  return (
    <button
      type="button"
      onClick={() => setToolbarLayout(isClassic ? 'compact' : 'classic')}
      aria-pressed={isClassic}
      data-testid="toggle-toolbar-layout"
      className={`flex items-center justify-center rounded-md h-9 w-9 shadow-sm transition-colors ${
        isClassic ? 'bg-primary/15 text-primary' : 'bg-surface-high hover:bg-surface-highest text-on-surface-variant'
      }`}
      title={t('toolbarLayout.toggleTitle', { layout: t(`toolbarLayout.${isClassic ? 'compact' : 'classic'}`) })}
    >
      <Row size={16} />
    </button>
  );
}
