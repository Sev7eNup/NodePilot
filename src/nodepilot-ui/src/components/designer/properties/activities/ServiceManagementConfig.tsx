import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

const ACTIONS = [
  { value: 'status', label: 'Get Status' },
  { value: 'start', label: 'Start' },
  { value: 'stop', label: 'Stop' },
  { value: 'restart', label: 'Restart' },
  { value: 'create', label: 'Create (anlegen)' },
  { value: 'delete', label: 'Delete (löschen)' },
  { value: 'setStartType', label: 'Set Startup Type (Startart)' },
] as const;

const STARTUP_TYPES = [
  { value: 'Automatic', label: 'Automatic' },
  { value: 'AutomaticDelayedStart', label: 'Automatic (Delayed Start)' },
  { value: 'Manual', label: 'Manual' },
  { value: 'Disabled', label: 'Disabled' },
] as const;

export function ServiceManagementConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const action = (config.action as string) || 'status';

  return (
    <>
      <FieldGrid>
        <VariableInsertField
          label="Service Name"
          value={(config.serviceName as string) || ''}
          onChange={(v) => onUpdate({ serviceName: v })}
          upstreamVars={upstreamVars}
          placeholder="Spooler"
        />
        <Field label="Action">
          <select
            value={action}
            onChange={(e) => onUpdate({ action: e.target.value })}
            className="input-field"
          >
            {ACTIONS.map((a) => (
              <option key={a.value} value={a.value}>{a.label}</option>
            ))}
          </select>
        </Field>
      </FieldGrid>

      {action === 'delete' && (
        <div className="text-[11px] text-error font-semibold leading-snug">
          ⚠ Stoppt den Dienst (falls aktiv) und entfernt ihn dauerhaft via <code className="bg-surface-high px-1 rounded">sc.exe delete</code>.
        </div>
      )}

      {action === 'create' && (
        <>
          <VariableInsertField
            label="Binary-Pfad (executable)"
            value={(config.binaryPath as string) || ''}
            onChange={(v) => onUpdate({ binaryPath: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools\\my-service.exe"
          />
          <VariableInsertField
            label="Display-Name (optional)"
            value={(config.displayName as string) || ''}
            onChange={(v) => onUpdate({ displayName: v })}
            upstreamVars={upstreamVars}
            placeholder="Mein Service"
          />
          <VariableInsertField
            label="Beschreibung (optional)"
            value={(config.description as string) || ''}
            onChange={(v) => onUpdate({ description: v })}
            upstreamVars={upstreamVars}
            placeholder="Was tut der Dienst?"
          />
          <Field label="Startart">
            <select
              value={(config.startupType as string) || 'Automatic'}
              onChange={(e) => onUpdate({ startupType: e.target.value })}
              className="input-field"
            >
              {STARTUP_TYPES.map((t) => (
                <option key={t.value} value={t.value}>{t.label}</option>
              ))}
            </select>
          </Field>
          <div className="text-[11px] text-on-surface-variant leading-snug">
            Hinweis: Der Dienst läuft per Default unter <code className="bg-surface-high px-1 rounded">LocalSystem</code>.
            Für andere Service-Accounts den Dienst nach dem Anlegen via <code className="bg-surface-high px-1 rounded">sc.exe config &lt;name&gt; obj=</code> anpassen.
          </div>
        </>
      )}

      {action === 'setStartType' && (
        <Field label="Startart">
          <select
            value={(config.startupType as string) || 'Automatic'}
            onChange={(e) => onUpdate({ startupType: e.target.value })}
            className="input-field"
          >
            {STARTUP_TYPES.map((t) => (
              <option key={t.value} value={t.value}>{t.label}</option>
            ))}
          </select>
        </Field>
      )}
    </>
  );
}
