import { DataBase } from '@carbon/icons-react';
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { dbAdminApi } from '../api/dbadmin';
import { TableList } from '../components/dbviewer/TableList';
import { TableGrid } from '../components/dbviewer/TableGrid';
import { QueryPane } from '../components/dbviewer/QueryPane';

type View =
  | { kind: 'browse'; tableName: string | null }
  | { kind: 'query' };

export function DbViewerPage() {
  const { t } = useTranslation(['database', 'common']);
  const [view, setView] = useState<View>({ kind: 'browse', tableName: null });
  // Increment a nonce alongside the table-name so QueryPane fires its insert effect
  // even when the same table is clicked twice in a row (effect would otherwise skip
  // the second click because the value didn't change).
  const [insertSignal, setInsertSignal] = useState<{ value: string; nonce: number } | undefined>(undefined);

  const { data: tables = [], isLoading, error } = useQuery({
    queryKey: ['dbadmin', 'tables'],
    queryFn: () => dbAdminApi.getTables(),
    staleTime: 30_000,
  });

  const activeTable = view.kind === 'browse'
    ? tables.find((t) => t.name === view.tableName) ?? null
    : null;

  if (isLoading) {
    return (
      <div className="flex-1 flex items-center justify-center text-on-surface-variant text-sm">
        {t('common:loading')}
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex-1 flex items-center justify-center text-red-600 text-sm">
        {t('database:errorLoad')}
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full max-w-[1600px] mx-auto gap-3">
      <header>
        <p className="text-sm text-on-surface-variant font-label whitespace-nowrap">{t('database:subtitle')}</p>
      </header>
      <div className="np-card flex flex-1 min-h-0 overflow-hidden">
        <TableList
          tables={tables}
          selectedTable={view.kind === 'browse' ? view.tableName : null}
          queryActive={view.kind === 'query'}
          onSelect={(name) => setView({ kind: 'browse', tableName: name })}
          onSelectQuery={() => setView({ kind: 'query' })}
          onInsertTableName={(name) => setInsertSignal({ value: name, nonce: Date.now() })}
        />

        <main className="flex-1 flex flex-col h-full overflow-hidden">
          {view.kind === 'query' ? (
            <QueryPane insertSignal={insertSignal} />
          ) : !activeTable ? (
            <div className="flex-1 flex flex-col items-center justify-center gap-3 text-on-surface-variant">
              <DataBase size={40} className="text-outline opacity-40" />
              <p className="text-sm font-label">{t('database:selectTable')}</p>
            </div>
          ) : (
            <TableGrid key={activeTable.name} table={activeTable} />
          )}
        </main>
      </div>
    </div>
  );
}
