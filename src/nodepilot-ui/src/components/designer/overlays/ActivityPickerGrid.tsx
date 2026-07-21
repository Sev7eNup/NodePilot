import { Close } from '@carbon/icons-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { buildActivityCategories } from '../library/activityCategories';
import { ActivityIcon } from '../library/NodeLibrary';

/**
 * Activity-picker grid used by EdgeInserter (centered overlay) and QuickConnectPicker
 * (positioned popup). Both share the same title-bar + 2-col grid + category filter
 * (no triggers, no annotations) — the only difference is the outer positioning, which
 * is owned by each caller.
 */
export function ActivityPickerGrid({ onPick, onClose }: Readonly<{
  onPick: (type: string, label: string) => void;
  onClose: () => void;
}>) {
  const { t, i18n } = useTranslation(['editor', 'common']);
  const insertableCategories = useMemo(
    () => buildActivityCategories().filter((c) => c.key !== 'triggers' && c.key !== 'annotations'),
    [i18n.language],
  );
  return (
    <>
      <div className="px-4 py-2.5 border-b border-outline-variant/20 flex items-center justify-between">
        <span className="font-headline text-sm font-bold text-on-surface">{t('editor:addNode')}</span>
        <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface" aria-label={t('common:close')}>
          <Close size={14} />
        </button>
      </div>
      <div className="p-2 space-y-3">
        {insertableCategories.map((cat) => (
          <div key={cat.key}>
            <h3 className="font-label text-[10px] font-bold text-outline uppercase tracking-widest px-2 mb-1">{cat.name}</h3>
            <div className="grid grid-cols-2 gap-1">
              {cat.items.map((item) => (
                <button
                  key={item.type}
                  onClick={() => onPick(item.type, item.label)}
                  className="flex items-center gap-2 px-2 py-1.5 rounded hover:bg-surface-high transition-colors text-left"
                >
                  <ActivityIcon type={item.type} size={18} />
                  <span className="font-label text-xs text-on-surface truncate">{item.label}</span>
                </button>
              ))}
            </div>
          </div>
        ))}
      </div>
    </>
  );
}
