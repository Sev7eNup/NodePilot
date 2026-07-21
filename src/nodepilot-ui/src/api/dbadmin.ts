import { api } from './client';

export interface DbAdminColumnInfo {
  name: string;
  clrType: string;
  isNullable: boolean;
  maxLength: number | null;
  isPrimaryKey: boolean;
  isMasked: boolean;
  isReadOnly: boolean;
}

export interface DbAdminCapabilities {
  canUpdate: boolean;
  canDelete: boolean;
}

export interface DbAdminTableInfo {
  name: string;
  displayName: string;
  /** Actual DB-level table name (e.g. "Credentials", not the EF singular "Credential").
   *  Used by the SQL query console — bare SQL has to match the physical table. */
  dbTableName: string;
  pkColumns: string[];
  capabilities: DbAdminCapabilities;
  columns: DbAdminColumnInfo[];
  rowCount: number;
  cascadeDeletesTo: string[];
}

export interface DbAdminRowsResponse {
  total: number;
  rows: Record<string, unknown>[];
}

export interface DbAdminInfo {
  provider: string;
  allowWriteQueries: boolean;
  queryTimeoutSeconds: number;
  queryMaxRows: number;
}

export interface DbAdminQueryColumn {
  name: string;
  type: string;
}

export interface DbAdminQueryResponse {
  columns: DbAdminQueryColumn[];
  rows: unknown[][];
  rowsAffected: number | null;
  durationMs: number;
  truncated: boolean;
  mode: 'read' | 'write';
}

export const dbAdminApi = {
  getTables: () => api.get<DbAdminTableInfo[]>('/dbadmin/tables'),

  getInfo: () => api.get<DbAdminInfo>('/dbadmin/info'),

  /**
   * Executes a SQL statement against the active database. Read-mode never persists
   * (server wraps everything in a rollback transaction). Write-mode requires the
   * server-side flag AND the X-Confirm-Write header — both are sent by this client.
   */
  query: (sql: string, mode: 'read' | 'write') => {
    if (mode === 'write') {
      return api.postWithHeaders<DbAdminQueryResponse>(
        '/dbadmin/query',
        { sql, mode },
        { 'Content-Type': 'application/json', 'X-Confirm-Write': 'ALLOW' },
      );
    }
    return api.post<DbAdminQueryResponse>('/dbadmin/query', { sql, mode });
  },

  getRows: (
    tableName: string,
    params: { skip?: number; take?: number; orderBy?: string; desc?: boolean },
  ) => {
    const q = new URLSearchParams();
    if (params.skip !== undefined) q.set('skip', String(params.skip));
    if (params.take !== undefined) q.set('take', String(params.take));
    if (params.orderBy) q.set('orderBy', params.orderBy);
    if (params.desc) q.set('desc', 'true');
    const qs = q.toString() ? `?${q.toString()}` : '';
    return api.get<DbAdminRowsResponse>(`/dbadmin/tables/${encodeURIComponent(tableName)}/rows${qs}`);
  },

  patchRow: (tableName: string, pk: string[], column: string, value: unknown) => {
    const pkParams = pk.map((v) => `pk=${encodeURIComponent(v)}`).join('&');
    return api.patch<void>(
      `/dbadmin/tables/${encodeURIComponent(tableName)}/rows?${pkParams}`,
      { column, value },
    );
  },

  deleteRow: (tableName: string, pk: string[]) => {
    const pkParams = pk.map((v) => `pk=${encodeURIComponent(v)}`).join('&');
    return api.delete<void>(
      `/dbadmin/tables/${encodeURIComponent(tableName)}/rows?${pkParams}`,
    );
  },
};
