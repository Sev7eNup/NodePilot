import { useEffect, useMemo } from 'react';
import type { ConfigProps } from '../shared';
import { Field, VariableInsertField } from '../shared';
import { FieldGrid } from '../panelChrome';

const OPERATIONS = [
  { value: 'append', label: 'Append (Zeile anhängen)' },
  { value: 'prepend', label: 'Prepend (Zeile voranstellen)' },
  { value: 'insert', label: 'Insert (an Zeilennummer einfügen)' },
  { value: 'replaceLine', label: 'Replace Line (ganze Zeile ersetzen)' },
  { value: 'delete', label: 'Delete (Zeile / Range / Pattern)' },
  { value: 'replace', label: 'Replace (Text suchen & ersetzen)' },
] as const;

const ENCODINGS = [
  { value: 'auto', label: 'Auto (BOM-sniff → fallback UTF-8)' },
  { value: 'utf8', label: 'UTF-8 (ohne BOM)' },
  { value: 'utf8-bom', label: 'UTF-8 (mit BOM)' },
  { value: 'utf16le', label: 'UTF-16 LE' },
  { value: 'utf16be', label: 'UTF-16 BE' },
  { value: 'ascii', label: 'ASCII' },
] as const;

const LINE_ENDINGS = [
  { value: 'preserve', label: 'Preserve (original beibehalten)' },
  { value: 'crlf', label: 'CRLF (Windows)' },
  { value: 'lf', label: 'LF (Unix)' },
] as const;

type DeleteMode = 'line' | 'range' | 'pattern';

/** Infer which delete sub-mode the saved config expresses. Falls back to "line" for fresh
 *  panels so the default isn't a dangerous match-pattern delete. */
function inferDeleteMode(config: Record<string, unknown>): DeleteMode {
  if (Array.isArray(config.lineRange)) return 'range';
  if (typeof config.matchPattern === 'string' && config.matchPattern.length > 0) return 'pattern';
  return 'line';
}

export function TextFileEditConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const operation = (config.operation as string) || 'append';
  const encoding = (config.encoding as string) || 'auto';
  const lineEnding = (config.lineEnding as string) || 'preserve';
  const createIfMissing = config.createIfMissing === true;
  const dryRun = config.dryRun === true;
  const useRegex = config.useRegex === true;
  const ignoreCase = config.ignoreCase === true;
  const occurrences = (config.occurrences as string) || 'all';
  const appendIfMissing = config.appendIfMissing === true;
  const appendIfMissingExact = config.appendIfMissingExact !== false; // default true
  const deleteMode = useMemo(() => inferDeleteMode(config), [config]);

  useEffect(() => {
    if (!config.operation) onUpdate({ operation: 'append' });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const supportsCreateIfMissing = operation === 'append' || operation === 'prepend';
  const needsContent = operation === 'append' || operation === 'prepend' || operation === 'insert' || operation === 'replaceLine';
  const needsLineNumber = operation === 'insert' || operation === 'replaceLine' || (operation === 'delete' && deleteMode === 'line');
  const needsLineRange = operation === 'delete' && deleteMode === 'range';
  const needsMatchPattern = operation === 'replace' || (operation === 'delete' && deleteMode === 'pattern');
  const needsReplace = operation === 'replace';

  const setDeleteMode = (mode: DeleteMode) => {
    const patch: Record<string, unknown> = {};
    // Clear the keys that don't belong to the new sub-mode so the activity-side validation
    // sees exactly one of {lineNumber, lineRange, matchPattern}. Without this the user could
    // toggle the segmented control and leave stale keys behind, which the C# validation would
    // reject as "accepts only one of …".
    if (mode !== 'line') patch.lineNumber = undefined;
    if (mode !== 'range') patch.lineRange = undefined;
    if (mode !== 'pattern') {
      patch.matchPattern = undefined;
      patch.useRegex = undefined;
    }
    onUpdate(patch);
  };

  const lineRangeArr = Array.isArray(config.lineRange) ? (config.lineRange as number[]) : [];

  return (
    <>
      <FieldGrid>
        <Field label="Operation">
          <select
            value={operation}
            onChange={(e) => onUpdate({ operation: e.target.value })}
            className="input-field"
          >
            {OPERATIONS.map((op) => (
              <option key={op.value} value={op.value}>{op.label}</option>
            ))}
          </select>
        </Field>
        <VariableInsertField
          label="Datei-Pfad"
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder="C:\\config\\app.conf"
        />
      </FieldGrid>

      {needsContent && (
        <VariableInsertField
          label={
            operation === 'replaceLine' ? 'Neuer Zeilen-Inhalt'
            : 'Inhalt (eine oder mehrere Zeilen, \\n-getrennt)'
          }
          value={(config.content as string) || ''}
          onChange={(v) => onUpdate({ content: v })}
          upstreamVars={upstreamVars}
          multiline
          rows={4}
          placeholder={operation === 'append' ? '127.0.0.1 nodepilot.local' : 'neuer Wert'}
          mono
        />
      )}

      {needsLineNumber && !needsLineRange && (
        <Field label="Zeilennummer (1-basiert)">
          <input
            type="number"
            min={1}
            value={(config.lineNumber as number | undefined) ?? ''}
            onChange={(e) => {
              const v = e.target.value === '' ? undefined : Number(e.target.value);
              onUpdate({ lineNumber: v });
            }}
            className="input-field"
            placeholder="z. B. 1 = erste Zeile"
          />
        </Field>
      )}

      {operation === 'delete' && (
        <Field label="Delete-Modus">
          <div className="inline-flex rounded-md border border-outline-variant overflow-hidden">
            {(['line', 'range', 'pattern'] as DeleteMode[]).map((mode) => (
              <button
                key={mode}
                type="button"
                onClick={() => setDeleteMode(mode)}
                className={
                  'px-3 py-1 text-xs ' +
                  (deleteMode === mode
                    ? 'bg-primary text-on-primary'
                    : 'bg-surface-container hover:bg-surface-high text-on-surface')
                }
              >
                {mode === 'line' && 'Einzelne Zeile'}
                {mode === 'range' && 'Zeilen-Range'}
                {mode === 'pattern' && 'Pattern-Match'}
              </button>
            ))}
          </div>
        </Field>
      )}

      {needsLineRange && (
        <FieldGrid>
          <Field label="Von (1-basiert, inkl.)">
            <input
              type="number"
              min={1}
              value={lineRangeArr[0] ?? ''}
              onChange={(e) => {
                const from = e.target.value === '' ? undefined : Number(e.target.value);
                onUpdate({ lineRange: from === undefined ? undefined : [from, lineRangeArr[1] ?? from] });
              }}
              className="input-field"
            />
          </Field>
          <Field label="Bis (1-basiert, inkl.)">
            <input
              type="number"
              min={1}
              value={lineRangeArr[1] ?? ''}
              onChange={(e) => {
                const to = e.target.value === '' ? undefined : Number(e.target.value);
                onUpdate({ lineRange: to === undefined ? undefined : [lineRangeArr[0] ?? to, to] });
              }}
              className="input-field"
            />
          </Field>
        </FieldGrid>
      )}

      {needsMatchPattern && (
        <VariableInsertField
          label={useRegex ? 'Match-Pattern (.NET Regex)' : 'Suchtext (literal)'}
          value={(config.matchPattern as string) || ''}
          onChange={(v) => onUpdate({ matchPattern: v })}
          upstreamVars={upstreamVars}
          placeholder={useRegex ? '^\\s*#' : '127.0.0.1 alt.host'}
          mono
        />
      )}

      {needsReplace && (
        <VariableInsertField
          label="Ersetzungs-Text (Captures via $1, $2, …)"
          value={(config.replace as string) || ''}
          onChange={(v) => onUpdate({ replace: v })}
          upstreamVars={upstreamVars}
          multiline
          rows={3}
          placeholder="neuer Wert"
          mono
        />
      )}

      {(needsMatchPattern || needsReplace) && (
        <FieldGrid>
          <Field label="Regex">
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={useRegex}
                onChange={(e) => onUpdate({ useRegex: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {useRegex ? '.NET Regex (PCRE-ähnlich)' : 'Literal-Suche'}
            </label>
          </Field>
          <Field label="Case-Insensitive">
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={ignoreCase}
                onChange={(e) => onUpdate({ ignoreCase: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {ignoreCase ? 'Groß-/Kleinschreibung ignorieren' : 'Exakt vergleichen'}
            </label>
          </Field>
        </FieldGrid>
      )}

      {needsReplace && (
        <Field label="Vorkommen">
          <div className="inline-flex rounded-md border border-outline-variant overflow-hidden">
            {(['all', 'first'] as const).map((mode) => (
              <button
                key={mode}
                type="button"
                onClick={() => onUpdate({ occurrences: mode })}
                className={
                  'px-3 py-1 text-xs ' +
                  (occurrences === mode
                    ? 'bg-primary text-on-primary'
                    : 'bg-surface-container hover:bg-surface-high text-on-surface')
                }
              >
                {mode === 'all' ? 'Alle' : 'Nur erstes'}
              </button>
            ))}
          </div>
        </Field>
      )}

      {operation === 'append' && (
        <FieldGrid>
          <Field label="Append nur wenn fehlt">
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={appendIfMissing}
                onChange={(e) => onUpdate({ appendIfMissing: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {appendIfMissing ? 'Idempotent — Zeile wird übersprungen falls vorhanden' : 'Immer anhängen'}
            </label>
          </Field>
          {appendIfMissing && (
            <Field label="Match-Strategie">
              <label className="flex items-center gap-2 py-1 text-sm">
                <input
                  type="checkbox"
                  checked={appendIfMissingExact}
                  onChange={(e) => onUpdate({ appendIfMissingExact: e.target.checked })}
                  className="w-4 h-4 rounded border-outline-variant accent-primary"
                />
                {appendIfMissingExact
                  ? 'Exakte Zeile (nach Trim)'
                  : 'Substring (case-insensitive — z. B. für hosts-File mit variabler IP)'}
              </label>
            </Field>
          )}
        </FieldGrid>
      )}

      <FieldGrid>
        <Field label="Encoding">
          <select
            value={encoding}
            onChange={(e) => onUpdate({ encoding: e.target.value })}
            className="input-field"
          >
            {ENCODINGS.map((enc) => (
              <option key={enc.value} value={enc.value}>{enc.label}</option>
            ))}
          </select>
        </Field>
        <Field label="Zeilenende">
          <select
            value={lineEnding}
            onChange={(e) => onUpdate({ lineEnding: e.target.value })}
            className="input-field"
          >
            {LINE_ENDINGS.map((le) => (
              <option key={le.value} value={le.value}>{le.label}</option>
            ))}
          </select>
        </Field>
      </FieldGrid>

      <FieldGrid>
        <Field label="Backup-Suffix (optional)">
          <input
            type="text"
            value={(config.backupSuffix as string) || ''}
            onChange={(e) => onUpdate({ backupSuffix: e.target.value || undefined })}
            className="input-field"
            placeholder=".bak"
          />
        </Field>
        {supportsCreateIfMissing && (
          <Field label="Datei anlegen falls fehlt">
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={createIfMissing}
                onChange={(e) => onUpdate({ createIfMissing: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {createIfMissing ? 'Leere Datei wird angelegt falls nicht vorhanden' : 'Fehler wenn Datei fehlt'}
            </label>
          </Field>
        )}
      </FieldGrid>

      <Field label="Dry-Run">
        <label className="flex items-center gap-2 py-1 text-sm">
          <input
            type="checkbox"
            checked={dryRun}
            onChange={(e) => onUpdate({ dryRun: e.target.checked })}
            className="w-4 h-4 rounded border-outline-variant accent-primary"
          />
          {dryRun ? 'Simulation — Datei wird nicht verändert, linesChanged wird berechnet' : 'Mutationen werden geschrieben'}
        </label>
      </Field>

      <div className="text-[11px] text-on-surface-variant leading-snug">
        Hinweis: maximale Dateigröße ist standardmäßig 50&nbsp;MB
        (<code className="bg-surface-high px-1 rounded">FileSystemOperation:TextEdit:MaxFileSizeMB</code>).
        Für größere Dateien <code className="bg-surface-high px-1 rounded">runScript</code> mit
        Stream-IO nutzen. Schreibvorgang ist atomar (Temp-Datei + Move), Dry-Run und Backup-Suffix
        machen Tests sicher.
      </div>
    </>
  );
}
