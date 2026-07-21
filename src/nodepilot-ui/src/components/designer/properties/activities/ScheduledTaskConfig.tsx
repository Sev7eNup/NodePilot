import { useEffect } from 'react';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

const ACTIONS = [
  { value: 'get', label: 'Get / Status' },
  { value: 'start', label: 'Start' },
  { value: 'stop', label: 'Stop' },
  { value: 'enable', label: 'Enable' },
  { value: 'disable', label: 'Disable' },
  { value: 'register', label: 'Register (anlegen)' },
  { value: 'unregister', label: 'Unregister (löschen)' },
] as const;

const TRIGGER_TYPES = [
  { value: 'once', label: 'Einmalig (once)' },
  { value: 'daily', label: 'Täglich (daily)' },
  { value: 'weekly', label: 'Wöchentlich (weekly)' },
  { value: 'atLogon', label: 'Bei Anmeldung (atLogon)' },
  { value: 'atStartup', label: 'Beim Systemstart (atStartup)' },
] as const;

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;

const RUN_LEVELS = [
  { value: 'limited', label: 'Limited' },
  { value: 'highest', label: 'Highest (elevated)' },
] as const;

export function ScheduledTaskConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const action = (config.action as string) || 'get';
  const triggerType = (config.triggerType as string) || 'daily';
  const daysOfWeek = (config.daysOfWeek as string[]) || [];
  const force = config.force === true;

  // Persist the visual default for `action` the moment the panel opens. Otherwise the
  // dropdown shows "Get / Status" but the saved JSON has no action key, and the run
  // fails with "unknown action ''". This matches what the user actually sees.
  useEffect(() => {
    if (config.action === undefined) {
      onUpdate({ action: 'get' });
    }
  }, [config.action, onUpdate]);

  // Same fix for triggerType: the register branch requires a triggerType, and the UI
  // shows "Täglich (daily)" as visual default. Without persistence the run would fail
  // with "unknown triggerType ''" once the user picks 'register' without touching the
  // trigger-type dropdown.
  useEffect(() => {
    if (action === 'register' && config.triggerType === undefined) {
      onUpdate({ triggerType: 'daily' });
    }
  }, [action, config.triggerType, onUpdate]);

  const toggleDay = (day: string) => {
    const next = daysOfWeek.includes(day)
      ? daysOfWeek.filter((d) => d !== day)
      : [...daysOfWeek, day];
    onUpdate({ daysOfWeek: next });
  };

  return (
    <>
      <VariableInsertField
        label="Task-Name"
        value={(config.taskName as string) || ''}
        onChange={(v) => onUpdate({ taskName: v })}
        upstreamVars={upstreamVars}
        placeholder="MyNightlyJob"
      />
      <VariableInsertField
        label="Task-Pfad (Ordner)"
        value={(config.taskPath as string) || '\\'}
        onChange={(v) => onUpdate({ taskPath: v })}
        upstreamVars={upstreamVars}
        placeholder="\\"
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

      {action === 'unregister' && (
        <div className="text-[11px] text-error font-semibold leading-snug">
          ⚠ Löscht den Task dauerhaft auf der Zielmaschine.
        </div>
      )}

      {action === 'register' && (
        <>
          <VariableInsertField
            label="Programm (executable)"
            value={(config.program as string) || ''}
            onChange={(v) => onUpdate({ program: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools\\my.exe"
          />
          <VariableInsertField
            label="Argumente (optional)"
            value={(config.arguments as string) || ''}
            onChange={(v) => onUpdate({ arguments: v })}
            upstreamVars={upstreamVars}
            placeholder="-quiet -log C:\\Logs\\out.log"
          />
          <VariableInsertField
            label="Working Directory (optional)"
            value={(config.workingDirectory as string) || ''}
            onChange={(v) => onUpdate({ workingDirectory: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools"
          />

          <Field label="Trigger-Typ">
            <select
              value={triggerType}
              onChange={(e) => onUpdate({ triggerType: e.target.value })}
              className="input-field"
            >
              {TRIGGER_TYPES.map((t) => (
                <option key={t.value} value={t.value}>{t.label}</option>
              ))}
            </select>
          </Field>

          {(triggerType === 'once' || triggerType === 'daily' || triggerType === 'weekly') && (
            <VariableInsertField
              label={triggerType === 'once'
                ? 'Start (yyyy-MM-ddTHH:mm:ss)'
                : 'Startzeit (HH:mm)'}
              value={(config.startTime as string) || ''}
              onChange={(v) => onUpdate({ startTime: v })}
              upstreamVars={upstreamVars}
              placeholder={triggerType === 'once' ? '2026-05-01T03:00:00' : '03:00'}
            />
          )}

          {triggerType === 'daily' && (
            <Field label="Intervall (Tage)">
              <input
                type="number"
                min={1}
                value={(config.daysInterval as number) || 1}
                onChange={(e) => onUpdate({ daysInterval: parseInt(e.target.value) || 1 })}
                className="input-field"
              />
            </Field>
          )}

          {triggerType === 'weekly' && (
            <>
              <Field label="Wochentage">
                <div className="flex flex-wrap gap-1">
                  {DAYS.map((d) => {
                    const active = daysOfWeek.includes(d);
                    return (
                      <button
                        key={d}
                        type="button"
                        onClick={() => toggleDay(d)}
                        className={`px-2.5 py-1 rounded-md text-xs font-label font-semibold transition-colors ${
                          active
                            ? 'bg-primary/15 text-primary'
                            : 'bg-surface-high hover:bg-surface-highest text-on-surface-variant'
                        }`}
                      >
                        {d.slice(0, 2)}
                      </button>
                    );
                  })}
                </div>
              </Field>
              <Field label="Intervall (Wochen)">
                <input
                  type="number"
                  min={1}
                  value={(config.weeksInterval as number) || 1}
                  onChange={(e) => onUpdate({ weeksInterval: parseInt(e.target.value) || 1 })}
                  className="input-field"
                />
              </Field>
            </>
          )}

          <VariableInsertField
            label="Run-As User"
            value={(config.runAsUser as string) || 'SYSTEM'}
            onChange={(v) => onUpdate({ runAsUser: v })}
            upstreamVars={upstreamVars}
            placeholder="SYSTEM / NETWORK SERVICE / DOMAIN\\User"
          />
          <Field label="Run-Level">
            <select
              value={(config.runLevel as string) || 'limited'}
              onChange={(e) => onUpdate({ runLevel: e.target.value })}
              className="input-field"
            >
              {RUN_LEVELS.map((r) => (
                <option key={r.value} value={r.value}>{r.label}</option>
              ))}
            </select>
          </Field>
          <VariableInsertField
            label="Description (optional)"
            value={(config.description as string) || ''}
            onChange={(v) => onUpdate({ description: v })}
            upstreamVars={upstreamVars}
            placeholder="Was tut der Task?"
          />

          <Field label="Force (überschreibt Task mit gleichem Namen)">
            <label className="flex items-start gap-2 cursor-pointer select-none py-1">
              <input
                type="checkbox"
                checked={force}
                onChange={(e) => onUpdate({ force: e.target.checked })}
                className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
              />
              <div className="flex-1 text-sm text-on-surface">
                {force ? 'Bestehender Task wird ersetzt.' : 'Schlägt fehl wenn Task existiert.'}
              </div>
            </label>
          </Field>

          <div className="text-[11px] text-on-surface-variant leading-snug">
            Hinweis: Domain-User funktionieren nur ohne Passwort wenn Resource-Based-Constrained-
            Delegation eingerichtet ist. Built-ins (<code className="bg-surface-high px-1 rounded">SYSTEM</code>,
            {' '}<code className="bg-surface-high px-1 rounded">NETWORK SERVICE</code>,
            {' '}<code className="bg-surface-high px-1 rounded">LOCAL SERVICE</code>) brauchen kein Passwort.
          </div>
        </>
      )}

      {action === 'get' && (
        <div className="text-[11px] text-on-surface-variant leading-snug">
          Output: <code className="bg-surface-high px-1 rounded">{`{{step.param.state}}`}</code>,
          {' '}<code className="bg-surface-high px-1 rounded">{`{{step.param.lastTaskResult}}`}</code>
          {' '}(0 = Erfolg), <code className="bg-surface-high px-1 rounded">{`{{step.param.nextRunTime}}`}</code>.
        </div>
      )}
    </>
  );
}
