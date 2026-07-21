import { Add, Locked, Terminal } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import type { DbAdminTableInfo } from '../../api/dbadmin';

interface Props {
  tables: DbAdminTableInfo[];
  selectedTable: string | null;
  /** Special value when the operator is on the Query Console pane. */
  queryActive: boolean;
  onSelect: (name: string) => void;
  onSelectQuery: () => void;
  /**
   * When the Query pane is active, clicking a table inserts its name into the editor
   * instead of switching the right pane. Returns true if the click was consumed.
   */
  onInsertTableName?: (name: string) => void;
}

export function TableList({
  tables, selectedTable, queryActive, onSelect, onSelectQuery, onInsertTableName,
}: Readonly<Props>) {
  const { t } = useTranslation(['database']);

  return (
    <aside className="w-60 shrink-0 border-r border-outline-variant/20 bg-surface-low flex flex-col h-full overflow-hidden">
      {/* ── Tools section ──
          Uniform sidebar background, no contrasting card — instead the Query button
          carries a left accent bar (primary colour) so it reads as "a different kind
          of thing" without breaking the visual rhythm with a hard-edged white box.
          A thin divider + margin gap to the Tables section seals the separation. */}
      <div className="px-4 pt-3 pb-1">
        <p className="text-[10px] font-label font-semibold text-outline uppercase tracking-wider">
          {t('database:toolsLabel')}
        </p>
      </div>
      <button
        type="button"
        onClick={onSelectQuery}
        className={`relative w-full flex items-center gap-2 pl-4 pr-4 py-2 text-sm text-left transition-colors ${
          queryActive
            ? 'bg-primary-fixed text-primary font-semibold'
            : 'text-on-surface hover:bg-surface-highest'
        }`}
      >
        {/* Left accent bar marks this as a primary tool/action — visible in both
            idle and active states, just dimmer when idle. */}
        <span
          aria-hidden
          className={`absolute left-0 top-1.5 bottom-1.5 w-0.5 rounded-r ${
            queryActive ? 'bg-primary' : 'bg-primary/40'
          }`}
        />
        <Terminal size={13} className={`shrink-0 ${queryActive ? 'text-primary' : 'text-primary/70'}`} />
        <span className="truncate font-label">{t('database:queryEntry')}</span>
      </button>
      {/* Divider + spacing between the two sections — hairline + small gap reads
          as "separator" without the heavy bordered-card vibe. */}
      <div className="mx-4 mt-3 mb-1 border-t border-outline-variant/40" />
      {/* ── Tables section ── */}
      <div className="px-4 pt-2 pb-1">
        <p className="text-[10px] font-label font-semibold text-outline uppercase tracking-wider">
          {t('database:tablesLabel', { count: tables.length })}
        </p>
      </div>
      <nav className="flex-1 overflow-y-auto pb-1">
        {tables.map((table) => {
          const isReadOnly = !table.capabilities.canUpdate && !table.capabilities.canDelete;
          const isActive = !queryActive && selectedTable === table.name;
          return (
            // Row layout: main click area opens the table (works in both browse and
            // query mode — so you always have a way out of the query view), and a
            // dedicated "+" icon-button on the right inserts the table name into
            // the editor when the query pane is showing. Keeping these on the same
            // row keeps the sidebar density unchanged.
            <div
              key={table.name}
              className={`group w-full flex items-center justify-between gap-2 px-2 pl-4 py-2 text-sm transition-colors ${
                isActive
                  ? 'bg-primary-fixed text-primary font-semibold'
                  : 'text-on-surface-variant hover:bg-surface-highest hover:text-on-surface'
              }`}
            >
              <button
                type="button"
                onClick={() => onSelect(table.name)}
                title={t('database:openTable', { name: table.displayName })}
                className="flex items-center gap-1.5 truncate font-label flex-1 min-w-0 text-left"
              >
                {isReadOnly && (
                  <span title={t('database:readOnly')} aria-label={t('database:readOnly')} className="shrink-0 inline-flex">
                    <Locked size={11} className="text-outline" />
                  </span>
                )}
                <span className="truncate">{table.displayName}</span>
              </button>
              <span className="shrink-0 text-[10px] font-mono text-outline tabular-nums">
                {table.rowCount.toLocaleString()}
              </span>
              {queryActive && onInsertTableName && (
                <button
                  type="button"
                  onClick={() => onInsertTableName(table.dbTableName)}
                  title={t('database:insertAtCursor', { name: table.dbTableName })}
                  className="shrink-0 p-0.5 rounded hover:bg-primary/10 text-outline hover:text-primary transition-colors"
                  aria-label={t('database:insertAria', { name: table.dbTableName })}
                >
                  <Add size={12} />
                </button>
              )}
            </div>
          );
        })}
      </nav>
    </aside>
  );
}
