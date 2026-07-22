import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { CodeField, FieldGrid } from '../panelChrome';

type ConnectionMode = 'builder' | 'raw';

/** Order used to decide which UI mode to show:
 *   1. an explicit `connectionMode` in the config blob wins,
 *   2. legacy fallback: a non-empty `connectionString` implies "raw" (auto-migrates old
 *      workflows that only ever had the connection-string mode),
 *   3. otherwise default to "builder" — fresh nodes show the server/database fields
 *      instead of an empty connection-string textarea.
 *  The backend only looks at which fields are present to decide how to connect — the
 *  `connectionMode` marker exists purely for UI state; the backend ignores it. */
function inferMode(config: Record<string, unknown>): ConnectionMode {
  if (config.connectionMode === 'raw') return 'raw';
  if (config.connectionMode === 'builder') return 'builder';
  if (typeof config.connectionString === 'string' && (config.connectionString as string).length > 0) return 'raw';
  return 'builder';
}

/** Which fields belong to "builder" mode per provider — these get cleared when the mode
 *  switches, so a stale value can't confuse the backend's connection resolution
 *  (the backend flips to "builder" mode automatically as soon as its pivot field is non-empty). */
const BUILDER_KEYS_BY_PROVIDER: Record<string, string[]> = {
  sqlserver: ['server', 'database', 'authentication', 'username', 'password', 'encrypt', 'trustServerCertificate'],
  postgres: ['host', 'port', 'database', 'username', 'password', 'sslMode'],
  sqlite: ['dataSource'],
};

export function SqlConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const provider = (config.provider as string) || 'sqlserver';
  const mode = inferMode(config);

  const switchToBuilder = () => {
    // Clear the raw connection-string field — otherwise the backend would see both, and the
    // builder fields might still be empty.
    onUpdate({ connectionMode: 'builder', connectionString: undefined });
  };

  const switchToRaw = () => {
    // Clear the builder fields for the current provider so the backend falls back to the
    // raw connection-string path.
    const patch: Record<string, unknown> = { connectionMode: 'raw' };
    for (const key of BUILDER_KEYS_BY_PROVIDER[provider] ?? []) patch[key] = undefined;
    onUpdate(patch);
  };

  const switchProvider = (next: string) => {
    // Discard the previous provider's fields so no leftover values pollute the JSON. The raw
    // connection string is deliberately kept — it's generic enough that it's up to the user
    // whether it still applies.
    const patch: Record<string, unknown> = { provider: next };
    for (const key of BUILDER_KEYS_BY_PROVIDER[provider] ?? []) patch[key] = undefined;
    onUpdate(patch);
  };

  const rawPlaceholder =
    provider === 'sqlite'
      ? 'Data Source=C:\\path\\to\\db.sqlite'
      : provider === 'postgres'
        ? 'Host=localhost;Database=mydb;Username=…;Password=…'
        : 'Server=.\\SQLEXPRESS;Database=MyDb;Integrated Security=True';

  return (
    <>
      <FieldGrid>
        <Field label={t('config.sql.provider')}>
          <select
            value={provider}
            onChange={(e) => switchProvider(e.target.value)}
            className="input-field"
          >
            <option value="sqlserver">{t('config.sql.providerSqlServer')}</option>
            <option value="sqlite">{t('config.sql.providerSqlite')}</option>
            <option value="postgres">{t('config.sql.providerPostgres')}</option>
          </select>
        </Field>
        <Field label={t('config.sql.connectionDefineAs')}>
          <div className="flex gap-1 rounded-md bg-surface-high p-0.5" role="tablist" aria-label={t('config.sql.connectionDefineAs')}>
            <ModeButton active={mode === 'builder'} onClick={switchToBuilder} label={t('config.sql.modeBuilder')} />
            <ModeButton active={mode === 'raw'} onClick={switchToRaw} label={t('config.sql.modeConnectionString')} />
          </div>
        </Field>
      </FieldGrid>

      {mode === 'builder' ? (
        <BuilderFields provider={provider} config={config} onUpdate={onUpdate} upstreamVars={upstreamVars} />
      ) : (
        <Field label={t('config.sql.connectionString')}>
          <VariableInsertField
            label=""
            value={(config.connectionString as string) || ''}
            onChange={(v) => onUpdate({ connectionString: v })}
            upstreamVars={upstreamVars}
            mono
            multiline
            rows={2}
            placeholder={rawPlaceholder}
          />
        </Field>
      )}

      <Field label={t('config.sql.query')}>
        <CodeField
          language="sql"
          value={(config.query as string) || ''}
          onChange={(v) => onUpdate({ query: v })}
          upstreamVars={upstreamVars}
          minLines={8}
          placeholder="SELECT Id, Name FROM Users WHERE Active = 1"
        />
      </Field>
    </>
  );
}

function ModeButton({ active, onClick, label }: Readonly<{ active: boolean; onClick: () => void; label: string }>) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={`flex-1 px-2 py-1 rounded text-xs font-label font-semibold transition-colors cursor-pointer ${
        active ? 'bg-surface-lowest text-on-surface shadow-sm' : 'text-on-surface-variant hover:bg-surface-highest'
      }`}
    >
      {label}
    </button>
  );
}

function BuilderFields({
  provider, config, onUpdate, upstreamVars,
}: Readonly<{
  provider: string;
  config: Record<string, unknown>;
  onUpdate: (patch: Record<string, unknown>) => void;
  upstreamVars: ConfigProps['upstreamVars'];
}>) {
  const { t } = useTranslation('properties');
  if (provider === 'sqlite') {
    return (
      <Field label={t('config.sql.dataSource')}>
        <VariableInsertField
          label=""
          value={(config.dataSource as string) || ''}
          onChange={(v) => onUpdate({ dataSource: v })}
          upstreamVars={upstreamVars ?? []}
          mono
          placeholder="C:\\NodePilot\\data\\inventory.db"
        />
      </Field>
    );
  }

  if (provider === 'postgres') {
    return (
      <FieldGrid>
        <Field label={t('config.sql.host')}>
          <VariableInsertField
            label=""
            value={(config.host as string) || ''}
            onChange={(v) => onUpdate({ host: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="pg01.example.com"
          />
        </Field>
        <Field label={t('config.sql.port')}>
          <input
            type="number"
            value={(config.port as number) || 5432}
            onChange={(e) => onUpdate({ port: parseInt(e.target.value) || 5432 })}
            className="input-field"
            min={1}
            max={65535}
          />
        </Field>
        <Field label={t('config.sql.database')}>
          <VariableInsertField
            label=""
            value={(config.database as string) || ''}
            onChange={(v) => onUpdate({ database: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="appdb"
          />
        </Field>
        <Field label={t('config.sql.sslMode')}>
          <select
            value={(config.sslMode as string) || 'Prefer'}
            onChange={(e) => onUpdate({ sslMode: e.target.value })}
            className="input-field"
          >
            <option value="Disable">Disable</option>
            <option value="Allow">Allow</option>
            <option value="Prefer">Prefer (Default)</option>
            <option value="Require">Require</option>
            <option value="VerifyCA">VerifyCA</option>
            <option value="VerifyFull">VerifyFull</option>
          </select>
        </Field>
        <Field label={t('config.sql.username')}>
          <VariableInsertField
            label=""
            value={(config.username as string) || ''}
            onChange={(v) => onUpdate({ username: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="postgres"
          />
        </Field>
        <Field label={t('config.sql.password')}>
          <VariableInsertField
            label=""
            value={(config.password as string) || ''}
            onChange={(v) => onUpdate({ password: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="{{globals.PG_PASSWORD}}"
            mono
          />
        </Field>
      </FieldGrid>
    );
  }

  // SQL Server
  const auth = (config.authentication as string) || 'integrated';
  return (
    <>
      <FieldGrid>
        <Field label={t('config.sql.server')}>
          <VariableInsertField
            label=""
            value={(config.server as string) || ''}
            onChange={(v) => onUpdate({ server: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="db01\\SQLEXPRESS"
          />
        </Field>
        <Field label={t('config.sql.database')}>
          <VariableInsertField
            label=""
            value={(config.database as string) || ''}
            onChange={(v) => onUpdate({ database: v })}
            upstreamVars={upstreamVars ?? []}
            placeholder="Reporting"
          />
        </Field>
        <Field label={t('config.sql.authentication')}>
          <select
            value={auth}
            onChange={(e) => {
              const next = e.target.value;
              // When switching to integrated auth, drop the SQL login fields — otherwise a
              // plaintext password would remain in the JSON.
              const patch: Record<string, unknown> = { authentication: next };
              if (next === 'integrated') {
                patch.username = undefined;
                patch.password = undefined;
              }
              onUpdate(patch);
            }}
            className="input-field"
          >
            <option value="integrated">{t('config.sql.authIntegrated')}</option>
            <option value="sql">{t('config.sql.authSql')}</option>
          </select>
        </Field>
        <Field label={t('config.sql.encryptTls')}>
          <div className="flex flex-col gap-1.5 pt-1">
            <label className="flex items-center gap-2 text-xs font-label">
              <input
                type="checkbox"
                checked={(config.encrypt as boolean | undefined) ?? true}
                onChange={(e) => onUpdate({ encrypt: e.target.checked })}
              />
              {t('config.sql.encryptLabel')}
            </label>
            <label className="flex items-center gap-2 text-xs font-label">
              <input
                type="checkbox"
                checked={(config.trustServerCertificate as boolean | undefined) ?? false}
                onChange={(e) => onUpdate({ trustServerCertificate: e.target.checked })}
              />
              {t('config.sql.trustServerCertificate')}
            </label>
          </div>
        </Field>
      </FieldGrid>
      {auth === 'sql' && (
        <FieldGrid>
          <Field label={t('config.sql.username')}>
            <VariableInsertField
              label=""
              value={(config.username as string) || ''}
              onChange={(v) => onUpdate({ username: v })}
              upstreamVars={upstreamVars ?? []}
              placeholder="svc-app"
            />
          </Field>
          <Field label={t('config.sql.password')}>
            <VariableInsertField
              label=""
              value={(config.password as string) || ''}
              onChange={(v) => onUpdate({ password: v })}
              upstreamVars={upstreamVars ?? []}
              placeholder="{{globals.SQL_PASSWORD}}"
              mono
            />
          </Field>
        </FieldGrid>
      )}
    </>
  );
}