import { useEffect, useMemo } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import type { ConfigProps } from '../shared';
import { Field, VariableInsertField } from '../shared';
import { FieldGrid } from '../panelChrome';

const OPERATIONS = [
  { value: 'append', key: 'opAppend' },
  { value: 'prepend', key: 'opPrepend' },
  { value: 'insert', key: 'opInsert' },
  { value: 'replaceLine', key: 'opReplaceLine' },
  { value: 'delete', key: 'opDelete' },
  { value: 'replace', key: 'opReplace' },
] as const;

const ENCODINGS = [
  { value: 'auto', key: 'encAuto' },
  { value: 'utf8', key: 'encUtf8' },
  { value: 'utf8-bom', key: 'encUtf8Bom' },
  { value: 'utf16le', key: 'encUtf16le' },
  { value: 'utf16be', key: 'encUtf16be' },
  { value: 'ascii', key: 'encAscii' },
] as const;

const LINE_ENDINGS = [
  { value: 'preserve', key: 'lePreserve' },
  { value: 'crlf', key: 'leCrlf' },
  { value: 'lf', key: 'leLf' },
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
  const { t } = useTranslation('properties');
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

  const deleteModeLabels: Record<DeleteMode, string> = {
    line: t('config.textFileEdit.deleteModeLine'),
    range: t('config.textFileEdit.deleteModeRange'),
    pattern: t('config.textFileEdit.deleteModePattern'),
  };

  return (
    <>
      <FieldGrid>
        <Field label={t('config.textFileEdit.operation')}>
          <select
            value={operation}
            onChange={(e) => onUpdate({ operation: e.target.value })}
            className="input-field"
          >
            {OPERATIONS.map((op) => (
              <option key={op.value} value={op.value}>{t(`config.textFileEdit.${op.key}`)}</option>
            ))}
          </select>
        </Field>
        <VariableInsertField
          label={t('config.textFileEdit.filePath')}
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder="C:\\config\\app.conf"
        />
      </FieldGrid>

      {needsContent && (
        <VariableInsertField
          label={
            operation === 'replaceLine'
              ? t('config.textFileEdit.contentLabelReplaceLine')
              : t('config.textFileEdit.contentLabelDefault')
          }
          value={(config.content as string) || ''}
          onChange={(v) => onUpdate({ content: v })}
          upstreamVars={upstreamVars}
          multiline
          rows={4}
          placeholder={operation === 'append' ? '127.0.0.1 nodepilot.local' : t('config.textFileEdit.contentPlaceholder')}
          mono
        />
      )}

      {needsLineNumber && !needsLineRange && (
        <Field label={t('config.textFileEdit.lineNumber')}>
          <input
            type="number"
            min={1}
            value={(config.lineNumber as number | undefined) ?? ''}
            onChange={(e) => {
              const v = e.target.value === '' ? undefined : Number(e.target.value);
              onUpdate({ lineNumber: v });
            }}
            className="input-field"
            placeholder={t('config.textFileEdit.lineNumberPlaceholder')}
          />
        </Field>
      )}

      {operation === 'delete' && (
        <Field label={t('config.textFileEdit.deleteMode')}>
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
                {deleteModeLabels[mode]}
              </button>
            ))}
          </div>
        </Field>
      )}

      {needsLineRange && (
        <FieldGrid>
          <Field label={t('config.textFileEdit.rangeFrom')}>
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
          <Field label={t('config.textFileEdit.rangeTo')}>
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
          label={useRegex ? t('config.textFileEdit.matchLabelRegex') : t('config.textFileEdit.matchLabelLiteral')}
          value={(config.matchPattern as string) || ''}
          onChange={(v) => onUpdate({ matchPattern: v })}
          upstreamVars={upstreamVars}
          placeholder={useRegex ? '^\\s*#' : '127.0.0.1 alt.host'}
          mono
        />
      )}

      {needsReplace && (
        <VariableInsertField
          label={t('config.textFileEdit.replace')}
          value={(config.replace as string) || ''}
          onChange={(v) => onUpdate({ replace: v })}
          upstreamVars={upstreamVars}
          multiline
          rows={3}
          placeholder={t('config.textFileEdit.contentPlaceholder')}
          mono
        />
      )}

      {(needsMatchPattern || needsReplace) && (
        <FieldGrid>
          <Field label={t('config.textFileEdit.regex')}>
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={useRegex}
                onChange={(e) => onUpdate({ useRegex: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {useRegex ? t('config.textFileEdit.regexOn') : t('config.textFileEdit.regexOff')}
            </label>
          </Field>
          <Field label={t('config.textFileEdit.caseInsensitive')}>
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={ignoreCase}
                onChange={(e) => onUpdate({ ignoreCase: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {ignoreCase ? t('config.textFileEdit.caseInsensitiveOn') : t('config.textFileEdit.caseInsensitiveOff')}
            </label>
          </Field>
        </FieldGrid>
      )}

      {needsReplace && (
        <Field label={t('config.textFileEdit.occurrences')}>
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
                {mode === 'all' ? t('config.textFileEdit.occurrencesAll') : t('config.textFileEdit.occurrencesFirst')}
              </button>
            ))}
          </div>
        </Field>
      )}

      {operation === 'append' && (
        <FieldGrid>
          <Field label={t('config.textFileEdit.appendIfMissing')}>
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={appendIfMissing}
                onChange={(e) => onUpdate({ appendIfMissing: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {appendIfMissing ? t('config.textFileEdit.appendIfMissingOn') : t('config.textFileEdit.appendIfMissingOff')}
            </label>
          </Field>
          {appendIfMissing && (
            <Field label={t('config.textFileEdit.matchStrategy')}>
              <label className="flex items-center gap-2 py-1 text-sm">
                <input
                  type="checkbox"
                  checked={appendIfMissingExact}
                  onChange={(e) => onUpdate({ appendIfMissingExact: e.target.checked })}
                  className="w-4 h-4 rounded border-outline-variant accent-primary"
                />
                {appendIfMissingExact
                  ? t('config.textFileEdit.matchStrategyExact')
                  : t('config.textFileEdit.matchStrategySubstring')}
              </label>
            </Field>
          )}
        </FieldGrid>
      )}

      <FieldGrid>
        <Field label={t('config.textFileEdit.encoding')}>
          <select
            value={encoding}
            onChange={(e) => onUpdate({ encoding: e.target.value })}
            className="input-field"
          >
            {ENCODINGS.map((enc) => (
              <option key={enc.value} value={enc.value}>{t(`config.textFileEdit.${enc.key}`)}</option>
            ))}
          </select>
        </Field>
        <Field label={t('config.textFileEdit.lineEnding')}>
          <select
            value={lineEnding}
            onChange={(e) => onUpdate({ lineEnding: e.target.value })}
            className="input-field"
          >
            {LINE_ENDINGS.map((le) => (
              <option key={le.value} value={le.value}>{t(`config.textFileEdit.${le.key}`)}</option>
            ))}
          </select>
        </Field>
      </FieldGrid>

      <FieldGrid>
        <Field label={t('config.textFileEdit.backupSuffix')}>
          <input
            type="text"
            value={(config.backupSuffix as string) || ''}
            onChange={(e) => onUpdate({ backupSuffix: e.target.value || undefined })}
            className="input-field"
            placeholder=".bak"
          />
        </Field>
        {supportsCreateIfMissing && (
          <Field label={t('config.textFileEdit.createIfMissing')}>
            <label className="flex items-center gap-2 py-1 text-sm">
              <input
                type="checkbox"
                checked={createIfMissing}
                onChange={(e) => onUpdate({ createIfMissing: e.target.checked })}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              {createIfMissing ? t('config.textFileEdit.createIfMissingOn') : t('config.textFileEdit.createIfMissingOff')}
            </label>
          </Field>
        )}
      </FieldGrid>

      <Field label={t('config.textFileEdit.dryRun')}>
        <label className="flex items-center gap-2 py-1 text-sm">
          <input
            type="checkbox"
            checked={dryRun}
            onChange={(e) => onUpdate({ dryRun: e.target.checked })}
            className="w-4 h-4 rounded border-outline-variant accent-primary"
          />
          {dryRun ? t('config.textFileEdit.dryRunOn') : t('config.textFileEdit.dryRunOff')}
        </label>
      </Field>

      <div className="text-[11px] text-on-surface-variant leading-snug">
        <Trans
          i18nKey="config.textFileEdit.maxSizeHint"
          ns="properties"
          components={[
            <code key={0} className="bg-surface-high px-1 rounded" />,
            <code key={1} className="bg-surface-high px-1 rounded" />,
          ]}
        />
      </div>
    </>
  );
}