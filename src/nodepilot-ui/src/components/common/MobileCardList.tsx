import { Fragment, type ReactNode } from 'react';

export interface MobileCardField {
  /** Short label shown in the left column of the card body. */
  label: ReactNode;
  /** The value cell — reuse the SAME renderers as the desktop table. */
  value: ReactNode;
  /** Span the full card width instead of the label/value two-column grid. */
  full?: boolean;
}

export interface MobileCardListProps<T> {
  items: T[];
  getKey: (item: T) => string;
  /** Card heading — typically the row's name plus any inline badges. */
  renderTitle: (item: T) => ReactNode;
  /** Label/value pairs rendered as a definition list inside the card. */
  renderFields: (item: T) => MobileCardField[];
  /** Optional action buttons, pinned to a footer row. */
  renderActions?: (item: T) => ReactNode;
  /** Optional whole-card tap handler (e.g. open a detail view). */
  onRowClick?: (item: T) => void;
}

/**
 * Mobile/phone replacement for the wide data tables on list pages. Each row becomes a
 * stacked card so 6–9 columns no longer force horizontal scrolling on a ~390px screen.
 *
 * Pages render `useIsMobile() ? <MobileCardList…/> : <table…/>` — the desktop table is
 * untouched, and the card branch reuses the page's existing cell renderers and handlers,
 * so there is no duplicated business logic. See `useIsMobile` in hooks/useMediaQuery.
 */
export function MobileCardList<T>({
  items, getKey, renderTitle, renderFields, renderActions, onRowClick,
}: Readonly<MobileCardListProps<T>>) {
  const clickable = !!onRowClick;
  return (
    <div className="space-y-2" data-testid="mobile-card-list">
      {items.map((item) => {
        const fields = renderFields(item);
        const actions = renderActions?.(item);
        return (
          <div
            key={getKey(item)}
            className={`np-card p-3 space-y-2 ${clickable ? 'cursor-pointer active:bg-surface-low' : ''}`}
            {...(clickable
              ? {
                  role: 'button',
                  tabIndex: 0,
                  onClick: () => onRowClick(item),
                  onKeyDown: (e: React.KeyboardEvent) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onRowClick(item); }
                  },
                }
              : {})}
          >
            <div className="min-w-0">{renderTitle(item)}</div>

            {fields.length > 0 && (
              <dl className="grid grid-cols-[minmax(0,auto)_1fr] gap-x-3 gap-y-1 text-sm">
                {fields.map((f, i) =>
                  f.full ? (
                    <div key={i} className="col-span-2 min-w-0">{f.value}</div>
                  ) : (
                    <Fragment key={i}>
                      <dt className="text-xs text-on-surface-variant/70 pt-0.5 truncate">{f.label}</dt>
                      <dd className="min-w-0 text-on-surface-variant">{f.value}</dd>
                    </Fragment>
                  ),
                )}
              </dl>
            )}

            {actions && (
              // Stop propagation so tapping an action button never triggers onRowClick.
              <div
                className="flex items-center justify-end gap-1 pt-2 border-t border-outline/30"
                onClick={(e) => e.stopPropagation()}
                onKeyDown={(e) => e.stopPropagation()}
                role="presentation"
              >
                {actions}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
