import { Close } from '@carbon/icons-react';
import { useState, useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import type { DbAdminColumnInfo } from '../../api/dbadmin';

interface Props {
  tableName: string;
  column: DbAdminColumnInfo;
  currentValue: unknown;
  onSave: (newValue: unknown) => void;
  onClose: () => void;
  isSaving: boolean;
}

export function EditCellDialog({ tableName, column, currentValue, onSave, onClose, isSaving }: Readonly<Props>) {
  const { t } = useTranslation(['database', 'common']);
  const [value, setValue] = useState<string>(formatForInput(currentValue, column.clrType));
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(null);

  useEffect(() => {
    setTimeout(() => inputRef.current?.focus(), 50);
  }, []);

  const isJson = isJsonColumn(column);
  const isLargeText = !isJson && (column.maxLength === null || column.maxLength > 200) && column.clrType === 'string';

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    let coerced: unknown = value;

    if (isJson) {
      try { coerced = JSON.parse(value); }
      catch { setError('Ungültiges JSON'); return; }
    } else if (column.clrType.startsWith('boolean')) {
      coerced = value === 'true' ? true : value === 'false' ? false : null;
    } else if (column.clrType.startsWith('int') || column.clrType.startsWith('long') ||
               column.clrType.startsWith('short') || column.clrType.startsWith('float') ||
               column.clrType.startsWith('double') || column.clrType.startsWith('decimal')) {
      // Number('') is 0 (not NaN) — catch an empty input before coercion, otherwise
      // a cleared nullable number cell would get saved as 0 instead of null.
      if (value === '') {
        if (column.isNullable) { coerced = null; }
        else { setError('Wert darf nicht leer sein'); return; }
      } else {
        const n = Number(value);
        if (isNaN(n)) { setError('Ungültige Zahl'); return; }
        coerced = n;
      }
    } else if (column.clrType.startsWith('datetime')) {
      if (!value) {
        coerced = null;
      } else {
        // Convert datetime-local (which is in local time) to UTC ISO string correctly.
        // Do NOT just append "Z" — that would misinterpret local time as UTC.
        const dt = new Date(value);
        if (isNaN(dt.getTime())) { setError('Ungültiges Datum'); return; }
        coerced = dt.toISOString();
      }
    } else if (value === '' && column.isNullable) {
      coerced = null;
    }

    onSave(coerced);
  }

  const inputClass = 'w-full px-3 py-2 border border-outline-variant rounded-md bg-surface text-on-surface text-sm focus:outline-none focus:ring-2 focus:ring-primary/50';

  function renderInput() {
    if (isJson || isLargeText) {
      return (
        <textarea
          ref={inputRef as React.Ref<HTMLTextAreaElement>}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          rows={8}
          className={`${inputClass} font-mono text-xs resize-y`}
          disabled={isSaving}
        />
      );
    }
    if (column.clrType.startsWith('boolean')) {
      return (
        <select
          ref={inputRef as React.Ref<HTMLSelectElement>}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          className={inputClass}
          disabled={isSaving}
        >
          {column.isNullable && <option value="">null</option>}
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      );
    }
    if (column.clrType.startsWith('datetime')) {
      return (
        <input
          ref={inputRef as React.Ref<HTMLInputElement>}
          type="datetime-local"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          className={inputClass}
          disabled={isSaving}
          step="1"
        />
      );
    }
    if (column.clrType.startsWith('int') || column.clrType.startsWith('long') ||
        column.clrType.startsWith('short') || column.clrType.startsWith('float') ||
        column.clrType.startsWith('double') || column.clrType.startsWith('decimal')) {
      return (
        <input
          ref={inputRef as React.Ref<HTMLInputElement>}
          type="number"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          className={inputClass}
          disabled={isSaving}
          step="any"
        />
      );
    }
    return (
      <input
        ref={inputRef as React.Ref<HTMLInputElement>}
        type="text"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        className={inputClass}
        disabled={isSaving}
        maxLength={column.maxLength ?? undefined}
      />
    );
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 backdrop-blur-sm"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="bg-surface rounded-xl shadow-xl border border-outline-variant/30 w-full max-w-lg mx-4"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-outline-variant/20">
          <div>
            <h2 className="font-headline font-semibold text-base text-on-surface">
              {t('database:editCell')}
            </h2>
            <p className="text-xs text-on-surface-variant font-mono mt-0.5">
              {tableName}.<strong>{column.name}</strong>
              <span className="ml-2 text-outline">({column.clrType})</span>
            </p>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-md text-on-surface-variant hover:bg-surface-highest transition-colors"
            disabled={isSaving}
          >
            <Close size={16} />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="px-5 py-4 space-y-4">
          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-1.5">
              {t('database:currentValue')}
            </label>
            <div className="px-3 py-2 rounded-md bg-surface-low border border-outline-variant/30 font-mono text-xs text-on-surface-variant break-all line-clamp-3">
              {currentValue === null || currentValue === undefined
                ? <span className="italic text-outline">null</span>
                : String(currentValue)}
            </div>
          </div>

          <div>
            <label className="block text-[10px] font-label font-semibold text-on-surface-variant uppercase tracking-wider mb-1.5">
              {t('database:newValue')}
            </label>
            {renderInput()}
            {error && (
              <p className="mt-1 text-xs text-red-600">{error}</p>
            )}
          </div>

          <div className="flex justify-end gap-2 pt-1">
            <button
              type="button"
              onClick={onClose}
              disabled={isSaving}
              className="px-4 py-2 rounded-md text-sm font-label font-medium text-on-surface-variant hover:bg-surface-highest transition-colors disabled:opacity-50"
            >
              {t('common:cancel')}
            </button>
            <button
              type="submit"
              disabled={isSaving}
              className="px-4 py-2 rounded-md text-sm font-label font-semibold bg-primary text-on-primary hover:bg-primary/90 transition-colors disabled:opacity-50"
            >
              {isSaving ? t('common:saving') : t('common:save')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function isJsonColumn(col: DbAdminColumnInfo): boolean {
  const lc = col.name.toLowerCase();
  return lc.endsWith('json') || lc === 'details' || lc === 'tags' || lc === 'returndata';
}

function formatForInput(value: unknown, clrType: string): string {
  if (value === null || value === undefined) return '';
  if (clrType.startsWith('datetime') && typeof value === 'string') {
    // Convert ISO UTC string to datetime-local format (strip "Z", keep to minutes)
    // new Date(isoString) parses correctly, then we convert back to local datetime-local value
    try {
      const dt = new Date(value as string);
      if (!isNaN(dt.getTime())) {
        // datetime-local expects "YYYY-MM-DDTHH:mm:ss" in local time
        const pad = (n: number) => String(n).padStart(2, '0');
        return `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}T${pad(dt.getHours())}:${pad(dt.getMinutes())}:${pad(dt.getSeconds())}`;
      }
    } catch { /* fall through */ }
  }
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
}
