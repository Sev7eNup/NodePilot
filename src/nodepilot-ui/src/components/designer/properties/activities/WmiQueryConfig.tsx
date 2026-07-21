import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

type WmiMode = 'query' | 'wql' | 'invokeMethod';

// Same identifier rules as the backend WmiQueryActivity.IdentifierPattern. Property names
// land unquoted in the projection script, so the UI guards against bad values too.
const CIM_IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

export function WmiQueryConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const mode = ((config.mode as WmiMode) || 'query');

  // Validate the JSON arguments blob inline so the user gets feedback before saving instead of
  // waiting for the engine to reject it. We accept empty (= no arguments) and any JSON object.
  const argumentsRaw = (config.argumentsRaw as string) ?? serializeArguments(config.arguments);
  const argumentsError = useMemo(() => validateArgumentsJson(argumentsRaw), [argumentsRaw]);

  // captureProperties is a JSON array on the config but easier to edit as comma-separated text.
  // We persist BOTH: the raw text (for round-trip editing) and the parsed array (for the engine).
  const capturePropertiesArr = Array.isArray(config.captureProperties)
    ? (config.captureProperties as unknown[]).filter((p): p is string => typeof p === 'string')
    : [];
  const capturePropertiesRaw = (config.capturePropertiesRaw as string) ?? capturePropertiesArr.join(', ');
  const captureError = useMemo(() => validateCaptureList(capturePropertiesRaw), [capturePropertiesRaw]);

  return (
    <>
      <Field label="Modus">
        <select
          value={mode}
          onChange={(e) => onUpdate({ mode: e.target.value as WmiMode })}
          className="input-field"
        >
          <option value="query">Klassen-Abfrage (Get-CimInstance)</option>
          <option value="wql">WQL-Abfrage (SELECT … FROM …)</option>
          <option value="invokeMethod">Methode aufrufen (Invoke-CimMethod)</option>
        </select>
      </Field>

      <FieldGrid>
        {(mode === 'query' || mode === 'invokeMethod') && (
          <VariableInsertField
            label={t('config.wmiQuery.wmiClass')}
            value={(config.className as string) || ''}
            onChange={(v) => onUpdate({ className: v })}
            upstreamVars={upstreamVars}
            placeholder="Win32_OperatingSystem"
          />
        )}
        <VariableInsertField
          label="Namespace"
          value={(config.namespace as string) || 'root\\cimv2'}
          onChange={(v) => onUpdate({ namespace: v })}
          upstreamVars={upstreamVars}
        />
      </FieldGrid>

      {mode === 'query' && (
        <VariableInsertField
          label="Filter (optional, WHERE-Klausel)"
          value={(config.filter as string) || ''}
          onChange={(v) => onUpdate({ filter: v })}
          upstreamVars={upstreamVars}
          placeholder="Name='explorer.exe'"
        />
      )}

      {mode === 'wql' && (
        <VariableInsertField
          label="WQL-Query"
          value={(config.query as string) || ''}
          onChange={(v) => onUpdate({ query: v })}
          upstreamVars={upstreamVars}
          multiline
          rows={4}
          mono
          placeholder="SELECT Name, ProcessId FROM Win32_Process WHERE WorkingSetSize > 100000000"
        />
      )}

      {(mode === 'query' || mode === 'wql') && (
        <Field label="captureProperties (optional, kommagetrennt)">
          <input
            type="text"
            value={capturePropertiesRaw}
            onChange={(e) => {
              const raw = e.target.value;
              const arr = raw
                .split(',')
                .map((s) => s.trim())
                .filter((s) => s.length > 0);
              // Persist both forms: raw text keeps the user's spacing/commas intact,
              // parsed array is what the engine reads. Drop `captureProperties` entirely
              // when empty so wmiQuery falls back to legacy raw-output mode.
              onUpdate({
                capturePropertiesRaw: raw,
                captureProperties: arr.length > 0 ? arr : undefined,
              });
            }}
            className="input-field font-mono text-xs"
            placeholder="Caption, BuildNumber, SMBIOSBIOSVersion"
          />
          {captureError ? (
            <p className="text-[11px] font-label text-red-600 dark:text-red-400 mt-1">{captureError}</p>
          ) : (
            <p className="text-[11px] font-label text-on-surface-variant mt-1">
              Macht die ersten Zeile als <code className="font-mono">{'{{step.param.<Name>}}'}</code> verfügbar
              (plus auto-emittiertes <code className="font-mono">param.count</code>). Property-Namen müssen
              gültige CIM-Identifier sein. Ohne dieses Feld bleibt nur die formatierte Text-Ausgabe in
              <code className="font-mono"> .output</code>.
            </p>
          )}
        </Field>
      )}

      {mode === 'invokeMethod' && (
        <>
          <FieldGrid>
            <VariableInsertField
              label="Methode"
              value={(config.methodName as string) || ''}
              onChange={(v) => onUpdate({ methodName: v })}
              upstreamVars={upstreamVars}
              placeholder="Create"
            />
            <VariableInsertField
              label="Filter (für Instanz-Methoden)"
              value={(config.filter as string) || ''}
              onChange={(v) => onUpdate({ filter: v })}
              upstreamVars={upstreamVars}
              placeholder="Name='notepad.exe'"
            />
          </FieldGrid>
          <Field label="Argumente (JSON-Objekt, optional)">
            <textarea
              value={argumentsRaw}
              onChange={(e) => {
                const raw = e.target.value;
                const parsed = tryParseArguments(raw);
                // Persist both the raw text (for round-trip editing) and the parsed object
                // (for the engine). When the JSON is invalid we drop `arguments` so the engine
                // doesn't see a stale value — the lint rule + inline error guard the save.
                onUpdate({ argumentsRaw: raw, arguments: parsed });
              }}
              className="input-field font-mono text-xs"
              rows={4}
              placeholder={'{ "CommandLine": "notepad.exe", "ShowWindow": true }'}
            />
            {argumentsError ? (
              <p className="text-[11px] font-label text-red-600 dark:text-red-400 mt-1">{argumentsError}</p>
            ) : (
              <p className="text-[11px] font-label text-on-surface-variant mt-1">
                Schlüssel = Methodenparameter (Buchstaben/Ziffern/_, nicht mit Ziffer beginnend). Werte: String, Zahl, true/false, null.
              </p>
            )}
          </Field>
        </>
      )}
    </>
  );
}

function serializeArguments(args: unknown): string {
  if (args === undefined || args === null) return '';
  if (typeof args !== 'object') return '';
  try {
    return JSON.stringify(args, null, 2);
  } catch {
    return '';
  }
}

function tryParseArguments(raw: string): Record<string, unknown> | undefined {
  const trimmed = raw.trim();
  if (!trimmed) return undefined;
  try {
    const parsed = JSON.parse(trimmed);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return undefined;
  } catch {
    return undefined;
  }
}

function validateCaptureList(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const entries = trimmed.split(',').map((s) => s.trim()).filter((s) => s.length > 0);
  const seen = new Set<string>();
  for (const e of entries) {
    if (!CIM_IDENTIFIER.test(e)) {
      return `Ungültiger Property-Name: "${e}" (Buchstaben/Ziffern/_, nicht mit Ziffer beginnend).`;
    }
    if (e.toLowerCase() === 'count') {
      return `"count" ist reserviert — die Activity emittiert es automatisch als Row-Total.`;
    }
    if (seen.has(e)) {
      return `Doppelter Eintrag: "${e}".`;
    }
    seen.add(e);
  }
  if (entries.length > 50) {
    return `Zu viele Properties (${entries.length}); Limit ist 50.`;
  }
  return null;
}

function validateArgumentsJson(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch (e) {
    return `Kein gültiges JSON: ${(e as Error).message}`;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return 'Argumente müssen ein JSON-Objekt sein (Key/Value-Paare).';
  }
  for (const key of Object.keys(parsed as Record<string, unknown>)) {
    if (!/^[A-Za-z_]\w*$/.test(key)) {
      return `Ungültiger Argument-Name: "${key}" (nur Buchstaben/Ziffern/_, nicht mit Ziffer beginnend).`;
    }
  }
  return null;
}
